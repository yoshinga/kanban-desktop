using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core;
using Kanban.Core.Models;

namespace Kanban.Desktop.ViewModels;

/// <summary>タグの分類・並び替え。</summary>
public static class TagRules
{
    public const string TrackerTag = "p-tracker";
    public static bool IsProject(string name) => name.StartsWith("p-");
    public static bool IsWork(string name) => name.StartsWith("w-");
    public static bool IsCategory(string name) => IsProject(name) || IsWork(name);

    /// <summary>カテゴリタグを先頭（w-* → p-*）、残りは名前順。</summary>
    public static IEnumerable<Tag> SortForDisplay(IEnumerable<Tag> tags) => tags
        .OrderBy(t => IsCategory(t.Name) ? 0 : 1)
        .ThenBy(t => IsWork(t.Name) ? 0 : 1)
        .ThenBy(t => t.Name, StringComparer.CurrentCulture);
}

public sealed class ProjectVm
{
    public required Project Model { get; init; }
    public string Name => Model.Name;
    public string Color => Model.Color;
}

/// <summary>サイドバーの 1 行（すべて / 今日 / カテゴリタグ）。</summary>
public sealed partial class SidebarTagVm : ObservableObject
{
    public Tag? Model { get; init; }
    public required string Label { get; init; }
    public required string Color { get; init; }
    public string? Emoji { get; init; }
    public bool HasEmoji => !string.IsNullOrEmpty(Emoji);
    /// <summary>件数 0 のとき淡色にするか（すべて / 今日 は常に通常色）。</summary>
    public bool DimWhenZero { get; init; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZero), nameof(HasCount))]
    private int _count;

    [ObservableProperty] private bool _selected;
    /// <summary>サイドバー検索に一致するか（一致しない行は隠す）。</summary>
    [ObservableProperty] private bool _matches = true;

    public bool IsZero => DimWhenZero && Count == 0;
    public bool HasCount => Count > 0;
}

/// <summary>ボード単位のタグ絞り込み（非カテゴリタグ）の 1 行。</summary>
public sealed partial class BoardFilterTagVm : ObservableObject
{
    public required string Name { get; init; }
    public required string Color { get; init; }
    [ObservableProperty] private bool _selected;
}

public sealed class SubtaskVm : ObservableObject
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    private bool _done;
    public bool Done { get => _done; set => SetProperty(ref _done, value); }
}

public sealed class CommentVm
{
    public required Guid Id { get; init; }
    public required string Author { get; init; }
    public required string Content { get; init; }
    public required string WhenText { get; init; }
    public bool IsAgent => Author != "user";
}

public sealed class ActivityVm
{
    public required string Action { get; init; }
    public required string Actor { get; init; }
    public required string WhenText { get; init; }
    public string? Details { get; init; }
}

public sealed class TagChipVm
{
    public Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Color { get; init; }
    public string? Emoji { get; init; }
    public string Display => (Emoji is null ? "" : Emoji + " ") + Name;
}

/// <summary>右クリックメニューのタグ行。</summary>
public sealed class TagMenuVm
{
    public required Tag Tag { get; init; }
    public required bool Attached { get; init; }
    public string Name => Tag.Name;
    public string Color => Tag.Color;
}

public sealed partial class ItemCardVm : ObservableObject
{
    public required ItemSummary Summary { get; init; }
    public Item Item => Summary.Item;
    public string Title => Item.Title;
    public long Number => Item.Number;
    public string NumberText => "#" + Number;
    /// <summary>パステル背景を決定するためのハッシュ元 (item id)。</summary>
    public string PastelKey => Item.Id.ToString();
    public bool IsDoneBoard { get; init; }
    public bool ShowNumbering { get; init; }
    /// <summary>Done ボードのカードは淡色表示（取り消し線）。</summary>
    public bool Dimmed => IsDoneBoard;
    /// <summary>ボード内の連番 (1 始まり)。表示は ShowNumbering のときのみ。</summary>
    public int? Sequence { get; init; }
    public bool ShowSequence => ShowNumbering && Sequence > 0;
    public bool TitleBold => ShowNumbering && Sequence == 1 && !Dimmed;
    public bool HighPriority => Item.Priority == Priorities.High;
    public bool ShowFlag => HighPriority && !Dimmed;
    public string? DueText { get; init; }
    /// <summary>overdue / today / soon / future。</summary>
    public string? DueStatus { get; init; }
    public bool HasDue => DueStatus is not null && !Dimmed;
    public bool ShowOverdueDot => DueStatus == "overdue" && !Dimmed;
    public bool HasDescription => !string.IsNullOrWhiteSpace(Item.Description);
    public List<TagChipVm> Tags { get; init; } = new();
    public List<TagChipVm> VisibleTags => Dimmed ? new() : Tags.Take(3).ToList();
    public int MoreTagsCount => Dimmed ? 0 : Math.Max(0, Tags.Count - 3);
    public bool HasSubtasks => Summary.SubtasksCount.Total > 0;
    public string SubtaskText => $"{Summary.SubtasksCount.Done}/{Summary.SubtasksCount.Total}";
    public bool HasComments => Summary.CommentsCount > 0;
    public int CommentsCount => Summary.CommentsCount;
    /// <summary>コメント数は 2 件以上のときだけ数字を出す。</summary>
    public bool ShowCommentsCount => Summary.CommentsCount > 1;
    public bool Archived => Item.Archived;
    public bool HasHeaderRow => ShowSequence || (!Dimmed && (HasDue || Tags.Count > 0))
                                || !string.IsNullOrEmpty(ContextText);
    public bool HasIndicators => !Dimmed && (HasDescription || HasSubtasks || HasComments);
    /// <summary>検索結果でのみ使う所属ボード名などのコンテキスト表示。</summary>
    public string? ContextText { get; set; }

    [ObservableProperty] private bool _dragging;
    /// <summary>ドロップ挿入位置インジケータ: "top" / "bottom" / null。</summary>
    [ObservableProperty] private string? _dropIndicator;
    /// <summary>落とし先スロットの高さ（つかんでいるカードと同じ高さで隙間を開ける）。</summary>
    [ObservableProperty] private double _slotHeight = 48;
    /// <summary>Ctrl+クリックで複数選択中か。</summary>
    [ObservableProperty] private bool _isSelected;
}

public partial class BoardVm : ObservableObject
{
    private bool _suppressFilter;

    public required Board Model { get; init; }
    /// <summary>
    /// 作成時点のボード表示属性の指紋。Model はサービス側と同じ参照で後から書き換わるので、
    /// 名前・絵文字などの変更検知はこちらと比べる。
    /// </summary>
    public string Key { get; init; } = "";
    /// <summary>グローバル絞り込み後・ボード内絞り込み前のカード。</summary>
    public List<ItemCardVm> SourceItems { get; private set; } = new();
    /// <summary>
    /// 表示中のカード（ボード内タグ絞り込み適用後）。更新時は 1 件ずつ Add せず
    /// コレクションごと差し替えて、ListBox の再生成を 1 回で済ませる。
    /// </summary>
    [ObservableProperty] private ObservableCollection<ItemCardVm> _items = new();
    public ObservableCollection<BoardFilterTagVm> FilterTags { get; } = new();
    [ObservableProperty] private string _newItemText = "";
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private bool _isDropTarget;
    [ObservableProperty] private bool _filterOpen;
    /// <summary>直近に反映したカード一覧の指紋。同じなら再構築しない。</summary>
    public string CardsSignature { get; private set; } = "";

    // ---- ページング（Done ボードのみ。下までスクロールしたら続きを取得） ----
    public const int PageSize = 30;
    /// <summary>取得する件数の上限。null は全件（Done 以外）。</summary>
    public int? Limit { get; set; }
    /// <summary>ボード上の総件数（未取得分を含む）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore), nameof(CountText), nameof(MoreText))]
    private int _total;
    /// <summary>取得済み件数（グローバル絞り込み前）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore), nameof(CountText), nameof(MoreText))]
    private int _loaded;
    public bool HasMore => Limit is not null && Loaded < Total;
    public string MoreText => $"↓ もっと読み込む（{Loaded}/{Total}）";
    public string Name => Model.Name;
    public string Color => Model.Color;
    public string? Emoji => Model.Emoji;
    public bool HasEmoji => !string.IsNullOrEmpty(Model.Emoji);
    public bool IsDone => Model.IsDone;
    public bool ShowNumbering => Model.ShowNumbering;
    public bool IsBoardLayout => Model.Layout == BoardLayouts.Board;
    public int Count => Items.Count;
    public bool HasLocalFilter => FilterTags.Any(t => t.Selected);
    public bool HasFilterTags => FilterTags.Count > 0;
    /// <summary>件数ピル: 絞り込み中は「表示/全体」。</summary>
    /// <summary>
    /// 件数ピル。通常のボードは絞り込み中「表示/全体」。ページング中のボードは絞り込み後の件数で数え、
    /// 未取得分があれば「+」を付ける。
    /// </summary>
    public string CountText => Limit is not null
        ? Items.Count + (HasMore ? "+" : "")
        : HasLocalFilter ? $"{Items.Count}/{SourceItems.Count}" : Items.Count.ToString();

    /// <summary>ページング中のボードでボード内のタグ絞り込みが変わった（取り直しが必要）。</summary>
    public event Action<BoardVm>? LocalFilterChanged;

    /// <summary>ボード内のタグ絞り込みの変更。ページング中は取り直しを依頼、それ以外は手元で絞る。</summary>
    private void OnLocalFilterToggled()
    {
        if (Limit is not null && LocalFilterChanged is not null)
        {
            LocalFilterChanged(this);
            OnPropertyChanged(nameof(HasLocalFilter));
        }
        else ApplyLocalFilter();
    }

    public void SetCards(List<ItemCardVm> cards, string signature)
    {
        SourceItems = cards;
        CardsSignature = signature;
        ApplyLocalFilter();
    }

    /// <summary>
    /// 続きのページを末尾に追加する。コレクションを差し替えないのでスクロール位置が保たれる。
    /// </summary>
    public void AppendCards(List<ItemCardVm> cards, string signature)
    {
        SourceItems.AddRange(cards);
        CardsSignature = signature;
        var active = FilterTags.Where(t => t.Selected).Select(t => t.Name).ToHashSet();
        foreach (var c in cards)
            if (active.Count == 0 || c.Tags.Any(t => active.Contains(t.Name)))
                Items.Add(c);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>ボード内フィルタ用タグの一覧を同期（変化がなければ何もしない）。</summary>
    public void SyncFilterTags(IReadOnlyList<Tag> tags)
    {
        if (FilterTags.Count == tags.Count
            && FilterTags.Zip(tags).All(p => p.First.Name == p.Second.Name && p.First.Color == p.Second.Color))
            return;
        var selected = FilterTags.Where(t => t.Selected).Select(t => t.Name).ToHashSet();
        FilterTags.Clear();
        foreach (var t in tags)
        {
            var ft = new BoardFilterTagVm { Name = t.Name, Color = t.Color, Selected = selected.Contains(t.Name) };
            ft.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(BoardFilterTagVm.Selected) && !_suppressFilter) OnLocalFilterToggled();
            };
            FilterTags.Add(ft);
        }
        OnPropertyChanged(nameof(HasFilterTags));
        if (selected.Count > 0) ApplyLocalFilter();
    }

    public void ApplyLocalFilter()
    {
        var active = FilterTags.Where(t => t.Selected).Select(t => t.Name).ToHashSet();
        Items = new ObservableCollection<ItemCardVm>(active.Count == 0
            ? SourceItems
            : SourceItems.Where(c => c.Tags.Any(t => active.Contains(t.Name))));
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasLocalFilter));
    }

    [RelayCommand]
    private void ClearLocalFilter()
    {
        _suppressFilter = true;
        try { foreach (var t in FilterTags) t.Selected = false; }
        finally { _suppressFilter = false; }
        OnLocalFilterToggled();
    }
}

public sealed class BoardOptionVm
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    /// <summary>絵文字を付けない素のボード名（"Assigned" などの名前照合用）。</summary>
    public string RawName { get; init; } = "";
    public bool IsDone { get; init; }
    public override string ToString() => Name;
}

public partial class ItemDetailViewModel : ObservableObject
{
    private readonly KanbanService _svc;
    private readonly Action _onChanged;
    private readonly Action<string> _notify;
    private bool _loading;
    private Guid _currentBoardId;
    private string _savedTitle = "";
    private string _savedDescription = "";
    private string _savedDue = "";
    public Guid ItemId { get; }

    public ItemDetailViewModel(KanbanService svc, Guid id, Action onChanged, Action<string> notify)
    {
        _svc = svc;
        ItemId = id;
        _onChanged = onChanged;
        _notify = notify;
        Load();
    }

    [ObservableProperty] private long _number;
    [ObservableProperty] private string _numberText = "";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _priority = Priorities.Normal;
    [ObservableProperty] private string _dueDateText = "";
    [ObservableProperty] private string? _dueStatus;
    [ObservableProperty] private string _dueStatusText = "";
    [ObservableProperty] private string _newSubtask = "";
    [ObservableProperty] private string _newComment = "";
    /// <summary>詳細のアクティビティ欄を開いているか（開くたびに畳んだ状態から始める）。</summary>
    [ObservableProperty] private bool _showActivity;
    [ObservableProperty] private string _newTag = "";
    [ObservableProperty] private bool _archived;
    [ObservableProperty] private string _boardName = "";
    [ObservableProperty] private BoardOptionVm? _selectedBoardOption;
    /// <summary>本文を編集中か（false のときは Markdown プレビュー。クリックで編集に入る）。</summary>
    [ObservableProperty] private bool _descEditing;
    [ObservableProperty] private bool _hasTagSuggestions;
    [ObservableProperty] private bool _canCreateTag;
    [ObservableProperty] private string _subtaskSummary = "";

    public ObservableCollection<SubtaskVm> Subtasks { get; } = new();
    public ObservableCollection<CommentVm> Comments { get; } = new();
    public ObservableCollection<ActivityVm> Activity { get; } = new();
    public ObservableCollection<TagChipVm> Tags { get; } = new();
    public ObservableCollection<BoardOptionVm> BoardOptions { get; } = new();
    public ObservableCollection<TagChipVm> TagSuggestions { get; } = new();

    public bool IsDueToday => DueDateText == DateTime.Today.ToString("yyyy-MM-dd");
    public bool IsDueTomorrow => DueDateText == DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
    public bool HasDueDate => !string.IsNullOrWhiteSpace(DueDateText);

    partial void OnNewTagChanged(string value) => UpdateTagSuggestions();

    partial void OnDueDateTextChanged(string value)
    {
        var (_, status) = MainViewModel.DueInfo(value);
        DueStatus = status;
        DueStatusText = status switch
        {
            "overdue" => "期限切れ",
            "today" => "今日",
            "soon" => "もうすぐ",
            "future" => "先",
            _ => "",
        };
        OnPropertyChanged(nameof(IsDueToday));
        OnPropertyChanged(nameof(IsDueTomorrow));
        OnPropertyChanged(nameof(HasDueDate));
    }

    private void UpdateTagSuggestions()
    {
        TagSuggestions.Clear();
        var q = NewTag.Trim();
        if (q.Length == 0)
        {
            HasTagSuggestions = false;
            CanCreateTag = false;
            return;
        }
        var attached = Tags.Select(t => t.Name).ToHashSet();
        var all = _svc.ListTags().Select(x => x.Tag).Where(t => !t.Archived).ToList();
        foreach (var t in all
                     .Where(t => !attached.Contains(t.Name)
                                 && t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(t => string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(t => t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                     .ThenByDescending(t => t.Favorite)
                     .ThenBy(t => t.Name)
                     .Take(8))
            TagSuggestions.Add(new TagChipVm { Id = t.Id, Name = t.Name, Color = t.Color, Emoji = t.Emoji });
        HasTagSuggestions = TagSuggestions.Count > 0;
        CanCreateTag = !all.Any(t => string.Equals(t.Name, q, StringComparison.Ordinal));
    }

    /// <summary>優先度ピルを切り替えたら即保存。</summary>
    partial void OnPriorityChanged(string value)
    {
        if (!_loading) Save();
    }

    /// <summary>ボード選択でアイテムを移動（末尾へ。Done は先頭へ）。</summary>
    partial void OnSelectedBoardOptionChanged(BoardOptionVm? value)
    {
        if (_loading || value is null || value.Id == _currentBoardId) return;
        SaveIfDirty();
        try
        {
            // Done はページ表示（先頭から 30 件）なので、末尾に入れると一覧から消えたように見える
            _svc.MoveItem(ItemId.ToString(), new ItemMove(value.Id, value.IsDone ? 0 : int.MaxValue), "user");
            _notify($"「{value.Name}」へ移動しました");
        }
        catch (KanbanException e)
        {
            MessageBox.Show(e.Message, "Kanban", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _onChanged();
        Load();
    }

    public void Load()
    {
        Item item;
        try { item = _svc.GetItem(ItemId.ToString()); }
        catch (NotFoundException) { return; }

        _loading = true;
        try
        {
            LoadCore(item);
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadCore(Item item)
    {
        Number = item.Number;
        NumberText = "#" + item.Number;
        Title = item.Title;
        Description = item.Description ?? "";
        Priority = item.Priority;
        DueDateText = item.DueDate ?? "";
        Archived = item.Archived;
        BoardName = TryBoardName(item.BoardId);
        _currentBoardId = item.BoardId;
        _savedTitle = Title;
        _savedDescription = Description;
        _savedDue = DueDateText;

        try
        {
            var board = _svc.GetBoard(item.BoardId);
            BoardOptions.Clear();
            foreach (var b in _svc.ListBoards(board.ProjectId))
                BoardOptions.Add(new BoardOptionVm
                {
                    Id = b.Id,
                    Name = string.IsNullOrEmpty(b.Emoji) ? b.Name : $"{b.Emoji} {b.Name}",
                    RawName = b.Name,
                    IsDone = b.IsDone,
                });
            SelectedBoardOption = BoardOptions.FirstOrDefault(o => o.Id == board.Id);
        }
        catch (NotFoundException) { }

        Subtasks.Clear();
        foreach (var s in item.Subtasks.OrderBy(s => s.Position))
            Subtasks.Add(new SubtaskVm { Id = s.Id, Title = s.Title, Done = s.Done });
        UpdateSubtaskSummary();

        Tags.Clear();
        foreach (var t in TagRules.SortForDisplay(item.Tags))
            Tags.Add(new TagChipVm { Id = t.Id, Name = t.Name, Color = t.Color, Emoji = t.Emoji });

        Comments.Clear();
        // 新しい順（上が最新）
        foreach (var c in _svc.CommentsForItem(item.Id).OrderByDescending(c => c.CreatedAt))
            Comments.Add(new CommentVm { Id = c.Id, Author = c.Author, Content = c.Content, WhenText = Fmt(c.CreatedAt) });

        Activity.Clear();
        foreach (var a in _svc.ListActivity(itemId: item.Id, limit: 30))
            Activity.Add(new ActivityVm
            {
                Action = MainViewModel.ActionLabel(a.Action),
                Actor = a.Actor,
                WhenText = Fmt(a.CreatedAt),
                Details = null,
            });
    }

    private void UpdateSubtaskSummary()
        => SubtaskSummary = Subtasks.Count == 0 ? "" : $"{Subtasks.Count(s => s.Done)}/{Subtasks.Count}";

    private string TryBoardName(Guid boardId)
    {
        try { return _svc.GetBoard(boardId).Name; }
        catch (NotFoundException) { return ""; }
    }

    public bool IsDirty =>
        Title != _savedTitle || Description != _savedDescription || DueDateText.Trim() != _savedDue;

    /// <summary>フォーカスアウト / パネルを閉じるときの自動保存。変更がなければ何もしない。</summary>
    public void SaveIfDirty()
    {
        if (IsDirty) Save();
    }

    [RelayCommand]
    public void Save()
    {
        var due = DueDateText.Trim();
        if (due.Length > 0 && !DateOnly.TryParseExact(due, "yyyy-MM-dd", out _))
        {
            _notify("期限は YYYY-MM-DD 形式で入力してください");
            DueDateText = _savedDue;
            due = _savedDue;
        }
        var body = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["title"] = string.IsNullOrWhiteSpace(Title) ? null : Title,
            ["description"] = Description.Length == 0 ? null : Description,
            ["priority"] = Priority,
            ["due_date"] = due.Length == 0 ? null : due,
        });
        try
        {
            _svc.UpdateItem(ItemId.ToString(), body, "user");
        }
        catch (KanbanException e)
        {
            MessageBox.Show(e.Message, "Kanban", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        _onChanged();
        Load();
    }

    [RelayCommand]
    public void ToggleSubtask(SubtaskVm? s)
    {
        if (s is null) return;
        _svc.UpdateSubtask(s.Id, JsonSerializer.SerializeToElement(new { done = s.Done }), "user");
        UpdateSubtaskSummary();
        _onChanged();
    }

    [RelayCommand]
    public void AddSubtask()
    {
        if (string.IsNullOrWhiteSpace(NewSubtask)) return;
        _svc.AddSubtask(ItemId.ToString(), NewSubtask.Trim(), "user");
        NewSubtask = "";
        _onChanged();
        Load();
    }

    [RelayCommand]
    public void DeleteSubtask(SubtaskVm? s)
    {
        if (s is null) return;
        _svc.DeleteSubtask(s.Id, "user");
        _onChanged();
        Load();
    }

    [RelayCommand]
    public void AddComment()
    {
        if (string.IsNullOrWhiteSpace(NewComment)) return;
        _svc.AddComment(ItemId.ToString(), new CommentCreate(NewComment.Trim(), "user"), "user");
        NewComment = "";
        _onChanged();
        Load();
    }

    /// <summary>コメントを投稿して、同じプロジェクトの Assigned ボードへ移動する（AI に渡す時の便利ボタン）。</summary>
    [RelayCommand]
    public void AddCommentAndAssign()
    {
        if (string.IsNullOrWhiteSpace(NewComment)) return;
        var assigned = BoardOptions.FirstOrDefault(o => string.Equals(o.RawName, "Assigned", StringComparison.OrdinalIgnoreCase));
        if (assigned is null)
        {
            MessageBox.Show("このプロジェクトに Assigned ボードがありません", "Kanban", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SaveIfDirty();
        _svc.AddComment(ItemId.ToString(), new CommentCreate(NewComment.Trim(), "user"), "user");
        NewComment = "";
        if (assigned.Id != _currentBoardId)
        {
            try
            {
                _svc.MoveItem(ItemId.ToString(), new ItemMove(assigned.Id, int.MaxValue), "user");
                _notify($"コメントを投稿して「{assigned.Name}」へ移動しました");
            }
            catch (KanbanException e)
            {
                MessageBox.Show(e.Message, "Kanban", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        _onChanged();
        Load();
    }

    [RelayCommand]
    public void DeleteComment(CommentVm? c)
    {
        if (c is null) return;
        _svc.DeleteComment(c.Id, "user");
        _onChanged();
        Load();
    }

    /// <summary>
    /// Enter での追加。完全一致する既存タグか、候補の先頭を付ける。
    /// 候補がない場合のみ新規作成（誤入力での新規タグ作成を防ぐ）。
    /// </summary>
    [RelayCommand]
    public void AttachTag()
    {
        var name = NewTag.Trim();
        if (name.Length == 0) return;
        if (!CanCreateTag || TagSuggestions.Count == 0)
            AttachByName(name);
        else
            AttachByName(TagSuggestions[0].Name);
    }

    /// <summary>入力した名前で新規タグを作成して付ける。</summary>
    [RelayCommand]
    public void CreateTag()
    {
        var name = NewTag.Trim();
        if (name.Length == 0) return;
        AttachByName(name);
    }

    [RelayCommand]
    public void AttachSuggestedTag(TagChipVm? chip)
    {
        if (chip is null) return;
        AttachByName(chip.Name);
    }

    private void AttachByName(string name)
    {
        SaveIfDirty();
        _svc.AttachTag(ItemId.ToString(), null, name, "user");
        NewTag = "";
        _onChanged();
        Load();
    }

    [RelayCommand]
    public void DetachTag(TagChipVm? chip)
    {
        if (chip is null) return;
        var tag = _svc.ListTags(includeArchived: true).FirstOrDefault(t => t.Tag.Name == chip.Name);
        if (tag is null) return;
        SaveIfDirty();
        _svc.DetachTag(ItemId.ToString(), tag.Tag.Id, "user");
        _onChanged();
        Load();
    }

    /// <summary>期限のクイック設定: today / tomorrow / week / clear。即保存する。</summary>
    [RelayCommand]
    public void SetDue(string? mode)
    {
        var today = DateTime.Today;
        DueDateText = mode switch
        {
            "today" => today.ToString("yyyy-MM-dd"),
            "tomorrow" => today.AddDays(1).ToString("yyyy-MM-dd"),
            "week" => today.AddDays(7).ToString("yyyy-MM-dd"),
            _ => "",
        };
        Save();
    }

    [RelayCommand]
    public void ToggleArchive()
    {
        SaveIfDirty();
        _svc.ArchiveItem(ItemId.ToString(), !Archived, "user");
        _notify(Archived ? "アーカイブを解除しました" : "アーカイブしました");
        _onChanged();
        Load();
    }

    [RelayCommand]
    public void CopyNumber()
    {
        try
        {
            Clipboard.SetText(NumberText);
            _notify($"{NumberText} をコピーしました");
        }
        catch { /* クリップボード競合は無視 */ }
    }

    /// <summary>削除したら true（呼び出し側でパネルを閉じる）。</summary>
    public bool Delete()
    {
        if (MessageBox.Show("このアイテムを削除しますか？", "Kanban",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return false;
        _svc.DeleteItem(ItemId.ToString(), "user");
        _notify("削除しました");
        _onChanged();
        return true;
    }

    internal static string Fmt(DateTimeOffset t)
    {
        var local = t.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm")
            : local.ToString("yyyy-MM-dd HH:mm");
    }
}

/// <summary>
/// 詳細を開いたまま右側に出す追加フォーム。追加に必要な項目（ボード・タイトル・期限・優先度・タグ）だけを持つ。
/// 追加後もフォームは開いたままにして、続けて追加できるようにする。
/// </summary>
public partial class QuickAddViewModel : ObservableObject
{
    private readonly KanbanService _svc;
    private readonly Action _onChanged;
    private readonly Action<string> _notify;

    public QuickAddViewModel(KanbanService svc, Guid projectId, IEnumerable<string> initialTags,
        Action onChanged, Action<string> notify)
    {
        _svc = svc;
        _onChanged = onChanged;
        _notify = notify;
        var boards = _svc.ListBoards(projectId);
        foreach (var b in boards)
            BoardOptions.Add(new BoardOptionVm
            {
                Id = b.Id,
                Name = string.IsNullOrEmpty(b.Emoji) ? b.Name : $"{b.Emoji} {b.Name}",
            });
        // 既定の追加先は Inbox（なければ先頭のボード）。必要ならフォームで変えられる
        var inbox = boards.FirstOrDefault(b => b.Name == "Inbox") ?? boards.OrderBy(b => b.Position).FirstOrDefault();
        SelectedBoardOption = BoardOptions.FirstOrDefault(o => o.Id == inbox?.Id) ?? BoardOptions.FirstOrDefault();
        foreach (var name in initialTags) AddTagByName(name);
    }

    [ObservableProperty] private BoardOptionVm? _selectedBoardOption;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _priority = Priorities.Normal;
    [ObservableProperty] private string _dueDateText = "";
    [ObservableProperty] private string _newTag = "";
    [ObservableProperty] private bool _hasTagSuggestions;

    public ObservableCollection<BoardOptionVm> BoardOptions { get; } = new();
    public ObservableCollection<TagChipVm> Tags { get; } = new();
    public ObservableCollection<TagChipVm> TagSuggestions { get; } = new();

    public bool IsDueToday => DueDateText == DateTime.Today.ToString("yyyy-MM-dd");
    public bool IsDueTomorrow => DueDateText == DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
    public bool HasDueDate => !string.IsNullOrWhiteSpace(DueDateText);

    partial void OnNewTagChanged(string value) => UpdateTagSuggestions();

    partial void OnDueDateTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsDueToday));
        OnPropertyChanged(nameof(IsDueTomorrow));
        OnPropertyChanged(nameof(HasDueDate));
    }

    /// <summary>候補は既存タグのみ（誤入力で新しいタグが増えないよう、このフォームからは作らない）。</summary>
    private void UpdateTagSuggestions()
    {
        TagSuggestions.Clear();
        var q = NewTag.Trim();
        if (q.Length > 0)
        {
            var attached = Tags.Select(t => t.Name).ToHashSet();
            foreach (var t in _svc.ListTags().Select(x => x.Tag)
                         .Where(t => !t.Archived && !attached.Contains(t.Name)
                                     && t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(t => string.Equals(t.Name, q, StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(t => t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                         .ThenByDescending(t => t.Favorite)
                         .ThenBy(t => t.Name)
                         .Take(6))
                TagSuggestions.Add(new TagChipVm { Id = t.Id, Name = t.Name, Color = t.Color, Emoji = t.Emoji });
        }
        HasTagSuggestions = TagSuggestions.Count > 0;
    }

    private void AddTagByName(string name)
    {
        if (Tags.Any(t => t.Name == name)) return;
        var tag = _svc.ListTags().Select(x => x.Tag).FirstOrDefault(t => t.Name == name);
        if (tag is null) return;
        Tags.Add(new TagChipVm { Id = tag.Id, Name = tag.Name, Color = tag.Color, Emoji = tag.Emoji });
    }

    /// <summary>入力欄で Enter: 先頭の候補を付ける。</summary>
    [RelayCommand]
    public void AttachTag()
    {
        if (TagSuggestions.Count == 0) return;
        AttachSuggestedTag(TagSuggestions[0]);
    }

    [RelayCommand]
    public void AttachSuggestedTag(TagChipVm? chip)
    {
        if (chip is null) return;
        AddTagByName(chip.Name);
        NewTag = "";
    }

    [RelayCommand]
    public void RemoveTag(TagChipVm? chip)
    {
        if (chip is not null) Tags.Remove(chip);
    }

    [RelayCommand]
    public void SetDue(string? which)
    {
        DueDateText = which switch
        {
            "today" => DateTime.Today.ToString("yyyy-MM-dd"),
            "tomorrow" => DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"),
            "week" => DateTime.Today.AddDays(7).ToString("yyyy-MM-dd"),
            _ => "",
        };
    }

    /// <summary>追加する。成功したらタイトルだけ空にして、ボード・期限・優先度・タグは次の追加用に残す。</summary>
    public bool Create()
    {
        var title = Title.Trim();
        if (title.Length == 0)
        {
            _notify("タイトルを入力してください");
            return false;
        }
        if (SelectedBoardOption is not { } board) return false;
        var due = DueDateText.Trim();
        if (due.Length > 0 && !DateOnly.TryParseExact(due, "yyyy-MM-dd", out _))
        {
            _notify("期限は YYYY-MM-DD 形式で入力してください");
            return false;
        }
        try
        {
            // 画面からの追加と同じくボードの先頭に入れる
            var item = _svc.CreateItem(new ItemCreate(board.Id, title,
                Priority: Priority,
                DueDate: due.Length > 0 ? due : null,
                Position: 0,
                Tags: Tags.Select(t => t.Name).ToList()), "user");
            Title = "";
            _notify($"#{item.Number} を「{board.Name}」に追加しました");
            _onChanged();
            return true;
        }
        catch (KanbanException e)
        {
            _notify("追加できませんでした: " + e.Message);
            return false;
        }
    }
}

public partial class MainViewModel : ObservableObject
{
    private readonly KanbanService _svc;
    private readonly UiState _ui;
    private readonly DispatcherTimer _noticeTimer;
    private readonly HashSet<string> _categoryFilter = new();
    private bool _reloadPending;

    public MainViewModel(KanbanService svc)
    {
        _svc = svc;
        _ui = UiState.Current;
        _sidebarCollapsed = _ui.SidebarCollapsed;
        _workGroupOpen = !_ui.WorkGroupClosed;
        _projectGroupOpen = !_ui.ProjectGroupClosed;
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _noticeTimer.Tick += (_, _) => { _noticeTimer.Stop(); Notice = ""; };
        _svc.Changed += (_, __) => Dispatch(RequestReload);
        LoadProjects();
    }

    public ObservableCollection<ProjectVm> Projects { get; } = new();
    public ObservableCollection<BoardVm> Boards { get; } = new();
    public ObservableCollection<SidebarTagVm> WorkTags { get; } = new();
    public ObservableCollection<SidebarTagVm> ProjectTags { get; } = new();
    public ObservableCollection<ItemCardVm> SearchResults { get; } = new();
    public ObservableCollection<ActivityVm> Activity { get; } = new();

    public SidebarTagVm AllEntry { get; } = new() { Label = "すべて", Color = "#94a3b8", DimWhenZero = false };
    public SidebarTagVm TodayEntry { get; } = new() { Label = "今日", Color = "#f97316", DimWhenZero = false };

    [ObservableProperty] private ProjectVm? _selectedProject;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isActivityView;
    [ObservableProperty] private bool _isTagsView;
    [ObservableProperty] private ItemDetailViewModel? _detail;
    /// <summary>詳細の右に出す追加フォーム（null のときは閉じている）。</summary>
    [ObservableProperty] private QuickAddViewModel? _quickAdd;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _todayFilter;
    [ObservableProperty] private bool _sidebarCollapsed;
    /// <summary>サイドバーの「仕事」「プロジェクト」グループが開いているか（ui.json に保存）。</summary>
    [ObservableProperty] private bool _workGroupOpen = true;
    [ObservableProperty] private bool _projectGroupOpen = true;
    [ObservableProperty] private int _untaggedCount;
    [ObservableProperty] private string _notice = "";

    /// <summary>サイドバーのタグ検索語。</summary>
    [ObservableProperty] private string _sidebarQuery = "";
    /// <summary>ボード設定モーダル（null で非表示）。</summary>
    [ObservableProperty] private BoardsAdminViewModel? _boardSettings;

    /// <summary>設定モーダル（文字サイズ・詳細の幅）を開いているか。</summary>
    [ObservableProperty] private bool _isSettingsOpen;

    public string UiScaleText => $"{Math.Round(UiZoom.Scale * 100)}%";

    public string DetailWidthText => UiState.Current.DetailWidth is null
        ? $"既定（{UiState.DefaultDetailWidth:0} px）"
        : $"{UiState.Current.DetailWidthOrDefault:0} px";

    /// <summary>倍率・詳細の幅が変わったら設定画面の表示を更新する。</summary>
    public void RefreshSettingsTexts()
    {
        OnPropertyChanged(nameof(UiScaleText));
        OnPropertyChanged(nameof(DetailWidthText));
    }

    public bool HasWorkTags => WorkTags.Any(t => t.Matches);
    public bool HasProjectTags => ProjectTags.Any(t => t.Matches);
    public bool IsSidebarFiltering => SidebarQuery.Trim().Length > 0;
    public bool SidebarNoMatch => IsSidebarFiltering && !HasWorkTags && !HasProjectTags;
    public bool ShowUntagged => UntaggedCount > 0 && !IsSidebarFiltering && (HasWorkTags || HasProjectTags);
    public bool HasNotice => Notice.Length > 0;
    // 閉じたグループでも、アイコンだけの折りたたみ表示（見出しが無い）とタグ検索中は中身を出す
    public bool ShowWorkItems => WorkGroupOpen || SidebarCollapsed || IsSidebarFiltering;
    public bool ShowProjectItems => ProjectGroupOpen || SidebarCollapsed || IsSidebarFiltering;

    partial void OnWorkGroupOpenChanged(bool value)
    {
        _ui.WorkGroupClosed = !value;
        _ui.Save();
        OnPropertyChanged(nameof(ShowWorkItems));
    }

    partial void OnProjectGroupOpenChanged(bool value)
    {
        _ui.ProjectGroupClosed = !value;
        _ui.Save();
        OnPropertyChanged(nameof(ShowProjectItems));
    }

    private void NotifyGroupItemsVisibility()
    {
        OnPropertyChanged(nameof(ShowWorkItems));
        OnPropertyChanged(nameof(ShowProjectItems));
    }

    partial void OnSelectedProjectChanged(ProjectVm? value)
    {
        if (value is not null)
        {
            _ui.ProjectId = value.Model.Id;
            _ui.Save();
        }
        Reload();
    }

    partial void OnSearchTextChanged(string value) => DoSearch();
    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(ShowBoards));
    partial void OnIsActivityViewChanged(bool value) => OnPropertyChanged(nameof(ShowBoards));
    partial void OnIsTagsViewChanged(bool value) => OnPropertyChanged(nameof(ShowBoards));

    private TagsAdminViewModel? _tagsAdmin;
    /// <summary>Tags 画面。</summary>
    public TagsAdminViewModel TagsAdmin => _tagsAdmin ??= new TagsAdminViewModel(_svc, ShowNotice);

    public void ShowTags()
    {
        if (IsSearching) SearchText = "";
        IsActivityView = false;
        IsTagsView = true;
        TagsAdmin.Sync();
        StatusText = $"タグ: {TagsAdmin.TotalCount} 件";
    }
    partial void OnUntaggedCountChanged(int value) => OnPropertyChanged(nameof(ShowUntagged));
    partial void OnNoticeChanged(string value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnSidebarCollapsedChanged(bool value)
    {
        if (value) SidebarQuery = "";
        _ui.SidebarCollapsed = value;
        _ui.Save();
        NotifyGroupItemsVisibility();
    }

    public bool ShowBoards => !IsSearching && !IsActivityView && !IsTagsView;

    private static void Dispatch(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    /// <summary>画面下部に一時的なトースト通知を出す。</summary>
    public void ShowNotice(string message)
    {
        Notice = message;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    public void LoadProjects()
    {
        Projects.Clear();
        foreach (var p in _svc.ListProjects())
            Projects.Add(new ProjectVm { Model = p });
        SelectedProject = Projects.FirstOrDefault(p => p.Model.Id == _ui.ProjectId) ?? Projects.FirstOrDefault();
        if (SelectedProject is null) Reload();
    }

    // ---- sidebar ----

    /// <summary>
    /// カテゴリタグの選択。null は「すべて」。通常クリックは単一選択（同じものを再クリックで解除）、
    /// Ctrl+クリックは集合に対するトグル。「今日」とは排他。
    /// </summary>
    public void SelectCategoryTag(SidebarTagVm? entry, bool additive)
    {
        TodayFilter = false;
        var name = entry?.Model?.Name;
        if (name is null)
        {
            _categoryFilter.Clear();
        }
        else if (additive)
        {
            if (!_categoryFilter.Remove(name)) _categoryFilter.Add(name);
        }
        else if (_categoryFilter.Count == 1 && _categoryFilter.Contains(name))
        {
            _categoryFilter.Clear();
        }
        else
        {
            _categoryFilter.Clear();
            _categoryFilter.Add(name);
        }
        if (IsSearching) SearchText = "";
        IsActivityView = false;
        IsTagsView = false;
        Reload();
    }

    public void SelectToday()
    {
        TodayFilter = !TodayFilter;
        _categoryFilter.Clear();
        if (IsSearching) SearchText = "";
        IsActivityView = false;
        IsTagsView = false;
        Reload();
    }

    private void UpdateSidebarSelection()
    {
        AllEntry.Selected = _categoryFilter.Count == 0 && !TodayFilter;
        TodayEntry.Selected = TodayFilter;
        foreach (var t in WorkTags.Concat(ProjectTags))
            t.Selected = t.Model is not null && _categoryFilter.Contains(t.Model.Name);
    }

    // ---- reload ----

    /// <summary>
    /// 再読み込みを予約する。同じフレーム内に何度呼ばれても Reload は 1 回だけ
    /// （保存 1 回で「詳細側の通知」と「サービスの変更イベント」が重なるため）。
    /// </summary>
    public void RequestReload()
    {
        if (IsDragging) { _reloadDeferred = true; return; }
        if (_reloadPending) return;
        _reloadPending = true;
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _reloadPending = false;
            if (IsDragging) { _reloadDeferred = true; return; }
            Reload();
        });
    }

    private bool _reloadDeferred;
    private bool _isDragging;

    /// <summary>
    /// カードをドラッグ中か。ドラッグ中に一覧を作り直すとドラッグ元のカードが画面から消えて
    /// ドラッグ操作が終わらなくなることがあるため、その間の再読み込みは終了後にまとめて行う。
    /// </summary>
    public bool IsDragging
    {
        get => _isDragging;
        set
        {
            _isDragging = value;
            if (!value && _reloadDeferred)
            {
                _reloadDeferred = false;
                RequestReload();
            }
        }
    }

    private static readonly string[] CardIncludes = { "tags", "subtasks_count", "comments_count" };

    /// <summary>先頭から limit 件を取得（null は全件）。サービス側の 1 回あたり上限 200 件を超える分は分割取得。</summary>
    private List<ItemSummary> FetchItems(Guid boardId, int? limit, out int total)
    {
        if (limit is null)
        {
            var all = _svc.ListItemsForBoard(boardId, includes: CardIncludes);
            total = all.Total;
            return all.Items;
        }
        var result = new List<ItemSummary>();
        total = 0;
        while (result.Count < limit)
        {
            var page = _svc.ListItemsForBoard(boardId, includes: CardIncludes,
                limit: Math.Min(200, limit.Value - result.Count), offset: result.Count);
            total = page.Total;
            result.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0) break;
        }
        return result;
    }

    private List<ItemSummary> ApplyGlobalFilter(List<ItemSummary> items, string today)
    {
        if (TodayFilter) return items.Where(s => IsTodayOrOverdue(s, today)).ToList();
        if (_categoryFilter.Count > 0)
            return items.Where(s => s.Tags.Any(t => _categoryFilter.Contains(t.Name))).ToList();
        return items;
    }

    /// <summary>Done ボードの続きを 1 ページ取得して末尾に追加する（一番下までスクロールしたとき）。</summary>
    public void LoadMore(BoardVm board)
    {
        if (!board.HasMore || board.Limit is not int limit) return;
        board.Limit = limit + BoardVm.PageSize;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var list = PageList(board, FetchItems(board.Model.Id, null, out _), today, out var total);
        board.Total = total;
        board.Loaded = list.Count;
        var sig = CardsSignature(list, today);

        // 既存分が先頭にそのまま並んでいれば差分だけ追加（スクロール位置を保つ）
        var old = board.SourceItems;
        var prefix = old.Count <= list.Count
                     && old.Select(c => c.Item.Id).SequenceEqual(list.Take(old.Count).Select(s => s.Item.Id));
        if (prefix)
        {
            var seq = old.Count + 1;
            board.AppendCards(list.Skip(old.Count).Select(s => Card(s, board.Model, seq++)).ToList(), sig);
        }
        else
        {
            var seq = 1;
            board.SetCards(list.Select(s => Card(s, board.Model, seq++)).ToList(), sig);
        }
        StatusText = $"{Boards.Sum(b => b.HasMore ? b.Total : b.Count)} 件";
    }

    /// <summary>
    /// ボードに表示するカードを決める。サイドバーの絞り込み（タグ / 今日）を先に掛け、
    /// ページング中のボード（Done）はさらにボード内のタグ絞り込みも掛けてから先頭 Limit 件に切る。
    /// 絞り込んだ結果に対してページングするので、「もっと読み込む」で絞り込み対象のカードが増え、
    /// 件数も絞り込み後の数になる。total は切る前の件数。
    /// </summary>
    private List<ItemSummary> PageList(BoardVm vm, List<ItemSummary> all, string today, out int total)
    {
        var list = ApplyGlobalFilter(all, today);
        if (vm.Limit is not int limit)
        {
            total = list.Count;
            return list;
        }
        var local = vm.FilterTags.Where(t => t.Selected).Select(t => t.Name).ToHashSet();
        if (local.Count > 0)
            list = list.Where(s => s.Tags.Any(t => local.Contains(t.Name))).ToList();
        total = list.Count;
        return list.Count > limit ? list.Take(limit).ToList() : list;
    }

    /// <summary>ページング中のボードでボード内のタグ絞り込みが変わったら、1 ページ目から取り直す。</summary>
    private void RefreshPagedBoard(BoardVm board)
    {
        board.Limit = BoardVm.PageSize;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var list = PageList(board, FetchItems(board.Model.Id, null, out _), today, out var total);
        board.Total = total;
        board.Loaded = list.Count;
        var seq = 1;
        board.SetCards(list.Select(s => Card(s, board.Model, seq++)).ToList(), CardsSignature(list, today));
        StatusText = $"{Boards.Sum(b => b.HasMore ? b.Total : b.Count)} 件";
    }

    private static string BoardKey(Board b) => $"{b.Name}|{b.Emoji}|{b.Color}|{b.IsDone}|{b.ShowNumbering}|{b.Layout}";

    /// <summary>カード表示に影響する値だけを連結した指紋（期限の状態は日付で変わるので today も含める）。</summary>
    private static string CardsSignature(List<ItemSummary> items, string today)
    {
        var sb = new StringBuilder(today, items.Count * 48);
        foreach (var s in items)
        {
            sb.Append('|').Append(s.Item.Id).Append(':').Append(s.Item.UpdatedAt.UtcTicks)
              .Append(':').Append(s.CommentsCount).Append(':').Append(s.SubtasksCount.Done)
              .Append('/').Append(s.SubtasksCount.Total);
            foreach (var t in s.Tags) sb.Append(',').Append(t.Name).Append(t.Color);
        }
        return sb.ToString();
    }

    public void Reload()
    {
        var allTags = _svc.ListTags().Select(t => t.Tag).ToList();
        if (IsTagsView)
        {
            TagsAdmin.Sync();
            return;
        }
        if (IsActivityView)
        {
            return;
        }
        if (IsSearching)
        {
            DoSearch();
            return;
        }
        if (SelectedProject is null) return;

        var boards = _svc.ListBoards(SelectedProject.Model.Id);
        var existing = Boards.ToDictionary(b => b.Model.Id);
        // 全件はメモリ上の一覧から取る（安価）。Done の表示はこのあと「絞り込み → ページ分だけ」に切り出す
        var itemsByBoard = boards.ToDictionary(b => b.Id, b => FetchItems(b.Id, null, out _));

        UpdateSidebar(allTags, boards, itemsByBoard);

        // 消えたタグをフィルタから掃除
        var tagNames = allTags.Select(t => t.Name).ToHashSet();
        _categoryFilter.RemoveWhere(n => !tagNames.Contains(n));
        UpdateSidebarSelection();

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var boardFilterTags = allTags.Where(t => !TagRules.IsCategory(t.Name)).OrderBy(t => t.Name).ToList();

        // ボード単位の差分更新: 変化のないボードは BoardVm もカードも作り直さない
        var next = new List<BoardVm>(boards.Count);
        foreach (var board in boards)
        {
            if (!existing.TryGetValue(board.Id, out var vm) || vm.Key != BoardKey(board))
            {
                vm = new BoardVm { Model = board, Key = BoardKey(board), Limit = board.IsDone ? BoardVm.PageSize : null };
                vm.LocalFilterChanged += RefreshPagedBoard;
            }

            var list = PageList(vm, itemsByBoard[board.Id], today, out var boardTotal);
            vm.Total = boardTotal;
            vm.Loaded = list.Count;
            var sig = CardsSignature(list, today);
            if (sig != vm.CardsSignature)
            {
                var seq = 1;
                vm.SetCards(list.Select(s => Card(s, board, seq++)).ToList(), sig);
            }
            vm.SyncFilterTags(boardFilterTags);
            next.Add(vm);
        }
        if (!Boards.SequenceEqual(next))
        {
            Boards.Clear();
            foreach (var vm in next) Boards.Add(vm);
        }

        var total = Boards.Sum(b => b.HasMore ? b.Total : b.Count);
        var filterNote = TodayFilter ? "（今日）"
            : _categoryFilter.Count > 0 ? $"（{string.Join(", ", _categoryFilter)}）" : "";
        StatusText = $"{total} 件" + filterNote;
    }

    /// <summary>Inbox 以外でサイドバーの件数に含めるボード名。</summary>
    private static readonly string[] PendingBoardNames = { "Assigned", "Doing", "Review" };

    /// <summary>
    /// サイドバーの件数集計。対象は未完了の Inbox + Assigned + Doing + Review。
    /// </summary>
    private void UpdateSidebar(List<Tag> allTags, List<Board> boards,
        Dictionary<Guid, List<ItemSummary>> itemsByBoard)
    {
        var pendingBoards = new List<Board>();
        var inbox = boards.FirstOrDefault(b => b.Name == "Inbox") ?? boards.OrderBy(b => b.Position).FirstOrDefault();
        if (inbox is not null) pendingBoards.Add(inbox);
        foreach (var name in PendingBoardNames)
        {
            var b = boards.FirstOrDefault(x => x.Name == name);
            if (b is not null && !pendingBoards.Contains(b)) pendingBoards.Add(b);
        }

        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var byTag = new Dictionary<string, int>();
        int total = 0, untagged = 0, todayCount = 0;
        foreach (var b in pendingBoards)
        {
            foreach (var it in itemsByBoard[b.Id])
            {
                total++;
                if (IsTodayOrOverdue(it, today)) todayCount++;
                var cats = it.Tags.Where(t => TagRules.IsCategory(t.Name)).ToList();
                if (cats.Count == 0) untagged++;
                foreach (var t in cats) byTag[t.Name] = byTag.GetValueOrDefault(t.Name) + 1;
            }
        }

        AllEntry.Count = total;
        TodayEntry.Count = todayCount;
        UntaggedCount = untagged;

        SyncSidebarGroup(WorkTags, allTags.Where(t => TagRules.IsWork(t.Name)), byTag);
        SyncSidebarGroup(ProjectTags, allTags.Where(t => TagRules.IsProject(t.Name)), byTag);
        ApplySidebarQuery();
    }

    partial void OnSidebarQueryChanged(string value)
    {
        ApplySidebarQuery();
        NotifyGroupItemsVisibility();
    }

    /// <summary>サイドバーのタグを検索語（名前・絵文字・説明の部分一致）で絞る。</summary>
    private void ApplySidebarQuery()
    {
        var q = SidebarQuery.Trim();
        foreach (var t in WorkTags.Concat(ProjectTags))
            t.Matches = q.Length == 0
                        || t.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (t.Emoji?.Contains(q) ?? false)
                        || (t.Model?.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
        OnPropertyChanged(nameof(HasWorkTags));
        OnPropertyChanged(nameof(HasProjectTags));
        OnPropertyChanged(nameof(IsSidebarFiltering));
        OnPropertyChanged(nameof(SidebarNoMatch));
        OnPropertyChanged(nameof(ShowUntagged));
    }

    /// <summary>サイドバー検索で Enter: 一致した先頭のタグを選ぶ。</summary>
    public void SelectFirstSidebarMatch()
    {
        var first = WorkTags.Concat(ProjectTags).FirstOrDefault(t => t.Matches);
        if (first is not null && !first.Selected) SelectCategoryTag(first, false);
    }

    // ---- board settings ----

    public void OpenBoardSettings(Guid? focusBoard = null)
    {
        if (SelectedProject is null) return;
        var vm = new BoardsAdminViewModel(_svc, SelectedProject.Model.Id, ShowNotice);
        vm.Sync();
        vm.FocusBoardId = focusBoard;
        BoardSettings = vm;
    }

    public void CloseBoardSettings()
    {
        BoardSettings = null;
        Reload();
    }

    private static void SyncSidebarGroup(ObservableCollection<SidebarTagVm> target, IEnumerable<Tag> tags,
        Dictionary<string, int> counts)
    {
        // お気に入りを先頭、同順位は名前順
        var sorted = tags.OrderByDescending(t => t.Favorite).ThenBy(t => t.Name, StringComparer.Ordinal).ToList();
        var same = target.Count == sorted.Count
                   && target.Zip(sorted).All(p => p.First.Model?.Id == p.Second.Id
                                                  && p.First.Label == p.Second.Name
                                                  && p.First.Color == p.Second.Color
                                                  && p.First.Emoji == p.Second.Emoji);
        if (!same)
        {
            target.Clear();
            foreach (var t in sorted)
                target.Add(new SidebarTagVm { Model = t, Label = t.Name, Color = t.Color, Emoji = t.Emoji });
        }
        foreach (var vm in target)
            vm.Count = counts.GetValueOrDefault(vm.Label);
    }

    private static bool IsTodayOrOverdue(ItemSummary s, string today)
    {
        var due = s.Item.DueDate;
        if (string.IsNullOrWhiteSpace(due)) return false;
        if (string.CompareOrdinal(due, today) > 0) return false;
        return !s.Tags.Any(t => t.Name == TagRules.TrackerTag);
    }

    private ItemCardVm Card(ItemSummary s, Board board, int sequence)
    {
        var (dueText, dueStatus) = DueInfo(s.Item.DueDate);
        return new ItemCardVm
        {
            IsSelected = _selection.ContainsKey(s.Item.Id),
            Summary = s,
            IsDoneBoard = board.IsDone,
            ShowNumbering = board.ShowNumbering,
            Sequence = sequence,
            DueText = dueText,
            DueStatus = dueStatus,
            Tags = TagRules.SortForDisplay(s.Tags)
                .Select(t => new TagChipVm { Id = t.Id, Name = t.Name, Color = t.Color, Emoji = t.Emoji }).ToList(),
        };
    }

    /// <summary>期限表示の 4 段階: overdue / today / soon(3日以内) / future。</summary>
    public static (string?, string?) DueInfo(string? due)
    {
        if (string.IsNullOrWhiteSpace(due) || !DateOnly.TryParseExact(due.Trim(), "yyyy-MM-dd", out var d))
            return (null, null);
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (d < today) return (DueLabel(d, today), "overdue");
        if (d == today) return ("今日", "today");
        if (d <= today.AddDays(3)) return (DueLabel(d, today), "soon");
        return (DueLabel(d, today), "future");
    }

    private static string DueLabel(DateOnly d, DateOnly today)
    {
        if (d == today.AddDays(1)) return "明日";
        if (d == today.AddDays(-1)) return "昨日";
        return d.Year == today.Year
            ? $"{d.Month}/{d.Day}"
            : $"{d.Year % 100}/{d.Month}/{d.Day}";
    }

    public void DoSearch()
    {
        var q = SearchText.Trim();
        SearchResults.Clear();
        IsSearching = q.Length > 0;
        if (!IsSearching) { Reload(); return; }
        // 画面は常に1つだけ。検索を開始したらアクティビティ / Tags 表示は畳む
        IsActivityView = false;
        IsTagsView = false;
        var result = _svc.Search(q, limit: 100);
        var boardsById = _svc.ListBoards().ToDictionary(b => b.Id);
        foreach (var hit in result.Results)
        {
            boardsById.TryGetValue(hit.Item.Item.BoardId, out var board);
            var card = Card(hit.Item, board ?? new Board(), 0);
            card.ContextText = board?.Name;
            SearchResults.Add(card);
        }
        StatusText = $"検索: {SearchResults.Count} 件";
    }

    public void ShowActivity()
    {
        // 検索中なら解除してから切り替える（画面の重なり防止）
        if (IsSearching) SearchText = "";
        IsActivityView = true;
        IsTagsView = false;
        IsSearching = false;
        Activity.Clear();
        var boardsById = _svc.ListBoards().ToDictionary(b => b.Id);
        foreach (var a in _svc.ListActivity(limit: 200))
            Activity.Add(new ActivityVm
            {
                Action = ActionLabel(a.Action),
                Actor = a.Actor,
                WhenText = a.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                Details = DetailsText(a, boardsById),
            });
        StatusText = $"アクティビティ: {Activity.Count} 件";
    }

    /// <summary>item.move のような内部表現を日本語ラベルに変換。</summary>
    public static string ActionLabel(string action) => action switch
    {
        "item.create" => "カードを追加",
        "item.update" => "カードを更新",
        "item.move" => "カードを移動",
        "item.archive" => "アーカイブ切替",
        "item.delete" => "カードを削除",
        "subtask.create" or "subtask.add" => "サブタスク追加",
        "subtask.toggle" => "サブタスク完了切替",
        "subtask.update" => "サブタスク更新",
        "subtask.delete" => "サブタスク削除",
        "comment.create" or "comment.add" => "コメント追加",
        "comment.delete" => "コメント削除",
        "tag.create" => "タグ作成",
        "tag.attach" => "タグ付け",
        "tag.detach" => "タグ外し",
        "board.create" => "ボード作成",
        "board.update" => "ボード更新",
        "board.delete" => "ボード削除",
        "project.create" => "プロジェクト作成",
        "project.update" => "プロジェクト更新",
        "project.delete" => "プロジェクト削除",
        "import.items" => "インポート",
        _ => action,
    };

    private static string? DetailsText(ActivityLog a, Dictionary<Guid, Board> boardsById)
    {
        if (a.Details is not { } d || d.ValueKind != JsonValueKind.Object) return null;
        try
        {
            // item.move: { from: { board_id }, to: { board_id } } → "Inbox → Done"
            if (a.Action == "item.move"
                && d.TryGetProperty("from", out var from) && d.TryGetProperty("to", out var to))
            {
                var fb = BoardNameOf(from, boardsById);
                var tb = BoardNameOf(to, boardsById);
                return $"{fb} → {tb}";
            }
            // item.update: { changed: { title: {from,to}, ... } } → 変更項目名の列挙
            if (d.TryGetProperty("changed", out var changed) && changed.ValueKind == JsonValueKind.Object)
            {
                var names = changed.EnumerateObject().Select(p => p.Name).ToList();
                return names.Count > 0 ? "変更: " + string.Join(", ", names) : null;
            }
            // title を持つ系はタイトルを出す
            if (d.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                return t.GetString();
        }
        catch { /* 表示用なので失敗しても握りつぶす */ }
        return null;
    }

    private static string BoardNameOf(JsonElement node, Dictionary<Guid, Board> boardsById)
    {
        try
        {
            if (node.TryGetProperty("board_id", out var idEl)
                && Guid.TryParse(idEl.GetString(), out var id)
                && boardsById.TryGetValue(id, out var b))
                return b.Name;
        }
        catch { }
        return "?";
    }

    public void SwitchToBoards()
    {
        if (IsSearching) SearchText = "";
        IsActivityView = false;
        IsTagsView = false;
        Reload();
    }

    public void OpenItem(ItemCardVm? card)
    {
        if (card is null) return;
        OpenItem(card.Item.Id);
    }

    public void OpenItem(Guid id)
    {
        Detail?.SaveIfDirty();
        Detail = new ItemDetailViewModel(_svc, id, RequestReload, ShowNotice);
    }

    public void CloseDetail()
    {
        // 編集内容は保存時点で再読み込みを予約済みなので、閉じるだけなら一覧は作り直さない
        Detail?.SaveIfDirty();
        QuickAdd = null;
        Detail = null;
    }

    public void DeleteDetailItem()
    {
        if (Detail?.Delete() == true)
        {
            QuickAdd = null;
            Detail = null;
            RequestReload();
        }
    }

    /// <summary>詳細の右の追加フォームを開閉する。既定の追加先は Inbox。</summary>
    public void ToggleQuickAdd()
    {
        if (QuickAdd is not null)
        {
            QuickAdd = null;
            return;
        }
        if (Detail is null || SelectedProject is null) return;
        // 絞り込み中はメイン画面の追加と同じく、そのタグ / 今日の期限を初期値にする
        var vm = new QuickAddViewModel(_svc, SelectedProject.Model.Id,
            TodayFilter ? Array.Empty<string>() : _categoryFilter.Take(1), RequestReload, ShowNotice);
        if (TodayFilter) vm.SetDue("today");
        QuickAdd = vm;
    }

    public void AddItem(BoardVm? board, string title)
    {
        if (board is null || string.IsNullOrWhiteSpace(title)) return;
        // 画面からの追加はボードの先頭に入れる（MCP / API からの追加は従来どおり末尾）
        var item = _svc.CreateItem(new ItemCreate(board.Model.Id, title.Trim(), Position: 0), "user");
        // 絞り込み中に追加したアイテムが条件から外れて「追加されない」ように見えるのを防ぐ:
        // タグで絞り込み中ならそのタグを、「今日」なら今日の期限を付ける
        if (TodayFilter)
            _svc.UpdateItem(item.Id.ToString(), JsonSerializer.SerializeToElement(
                new Dictionary<string, object?> { ["due_date"] = DateTime.Today.ToString("yyyy-MM-dd") }), "user");
        else if (_categoryFilter.Count > 0)
            _svc.AttachTag(item.Id.ToString(), null, _categoryFilter.First(), "user");
        Reload();
    }

    public void MoveItem(ItemCardVm card, BoardVm targetBoard, int index)
    {
        _svc.MoveItem(card.Item.Id.ToString(), new ItemMove(targetBoard.Model.Id, index), "user");
        Reload();
    }

    // ---- 複数選択（Ctrl+クリック） ----

    /// <summary>選択中のアイテム（ID → 番号）。一覧を作り直しても選択が残るよう ID で持つ。</summary>
    private readonly Dictionary<Guid, long> _selection = new();

    public int SelectedCount => _selection.Count;
    public bool HasSelection => _selection.Count > 0;
    public bool IsCardSelected(ItemCardVm card) => _selection.ContainsKey(card.Item.Id);

    private IEnumerable<ItemCardVm> AllCards => Boards.SelectMany(b => b.SourceItems).Concat(SearchResults);

    public void ToggleCardSelection(ItemCardVm card)
    {
        var id = card.Item.Id;
        if (!_selection.Remove(id)) _selection[id] = card.Number;
        var selected = _selection.ContainsKey(id);
        foreach (var c in AllCards.Where(c => c.Item.Id == id)) c.IsSelected = selected;
        NotifySelection();
    }

    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        _selection.Clear();
        foreach (var c in AllCards) c.IsSelected = false;
        NotifySelection();
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }

    /// <summary>選択中のアイテム番号を「kanban 1000, 1001, 1002」の形でコピーする（番号順）。</summary>
    public void CopySelectedNumbers()
    {
        if (_selection.Count == 0) return;
        var text = "kanban " + string.Join(", ", _selection.Values.Order());
        try
        {
            Clipboard.SetText(text);
            ShowNotice($"{_selection.Count} 件の番号をコピーしました");
        }
        catch { /* クリップボード競合は無視 */ }
    }

    // ---- card context menu ----

    /// <summary>カードの番号を「kanban 123」の形でコピーする（まとめてコピーと同じ形。そのままエージェントへの依頼に貼れるように）。</summary>
    public void CopyCardNumber(ItemCardVm card)
    {
        try
        {
            var text = "kanban " + card.Number;
            Clipboard.SetText(text);
            ShowNotice($"{text} をコピーしました");
        }
        catch { /* クリップボード競合は無視 */ }
    }

    public List<TagMenuVm> TagMenuFor(ItemCardVm card)
    {
        var attached = card.Tags.Select(t => t.Name).ToHashSet();
        return TagRules.SortForDisplay(_svc.ListTags().Select(t => t.Tag).Where(t => !t.Archived))
            .OrderByDescending(t => attached.Contains(t.Name))
            .Select(t => new TagMenuVm { Tag = t, Attached = attached.Contains(t.Name) })
            .ToList();
    }

    public void ToggleCardTag(ItemCardVm card, Tag tag, bool attached)
    {
        if (attached) _svc.DetachTag(card.Item.Id.ToString(), tag.Id, "user");
        else _svc.AttachTag(card.Item.Id.ToString(), tag.Id, null, "user");
        Reload();
    }

    public void SetCardDue(ItemCardVm card, string? mode)
    {
        var today = DateTime.Today;
        string? due = mode switch
        {
            "today" => today.ToString("yyyy-MM-dd"),
            "tomorrow" => today.AddDays(1).ToString("yyyy-MM-dd"),
            "week" => today.AddDays(7).ToString("yyyy-MM-dd"),
            _ => null,
        };
        _svc.UpdateItem(card.Item.Id.ToString(),
            JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["due_date"] = due }), "user");
        Reload();
    }
}
