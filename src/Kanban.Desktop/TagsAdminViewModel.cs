using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core;
using Kanban.Core.Models;

namespace Kanban.Desktop.ViewModels;

/// <summary>親タグ選択肢（null = なし）。</summary>
public sealed class ParentOptionVm
{
    public Guid? Id { get; init; }
    public required string Name { get; init; }
    public override string ToString() => Name;
}

/// <summary>Tags 画面の 1 行。テキスト欄はフォーカスアウト / Enter で確定して保存する。</summary>
public sealed partial class TagRowVm : ObservableObject
{
    private readonly TagsAdminViewModel _owner;
    private bool _loading;

    public TagRowVm(TagsAdminViewModel owner, Tag tag)
    {
        _owner = owner;
        Load(tag);
    }

    public Tag Model { get; private set; } = new();
    public Guid Id => Model.Id;
    public string Color => Model.Color;
    public bool Favorite => Model.Favorite;
    public bool Archived => Model.Archived;
    /// <summary>カテゴリタグ (p-* / w-*) 以外は親タグを持てる。</summary>
    public bool CanHaveParent => !TagRules.IsCategory(Model.Name);

    // 編集バッファ
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _emoji = "";
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _hexInput = "";
    [ObservableProperty] private bool _colorOpen;
    [ObservableProperty] private ParentOptionVm? _selectedParent;

    public void Load(Tag tag)
    {
        _loading = true;
        try
        {
            Model = tag;
            Name = tag.Name;
            Emoji = tag.Emoji ?? "";
            Path = tag.Path ?? "";
            Description = tag.Description ?? "";
            HexInput = tag.Color;
            SelectedParent = _owner.ParentOptions.FirstOrDefault(p => p.Id == tag.ParentTagId)
                             ?? _owner.ParentOptions.FirstOrDefault();
            OnPropertyChanged(nameof(Color));
            OnPropertyChanged(nameof(Favorite));
            OnPropertyChanged(nameof(Archived));
            OnPropertyChanged(nameof(CanHaveParent));
        }
        finally { _loading = false; }
    }

    /// <summary>未確定の編集があるか（外部変更で上書きしないため）。</summary>
    public bool IsDirty =>
        Name != Model.Name || Emoji != (Model.Emoji ?? "") || Path != (Model.Path ?? "")
        || Description != (Model.Description ?? "");

    partial void OnSelectedParentChanged(ParentOptionVm? value)
    {
        if (_loading || value is null || value.Id == Model.ParentTagId) return;
        _owner.Update(this, new Dictionary<string, object?> { ["parent_tag_id"] = value.Id });
    }

    public void CommitName()
    {
        var v = Name.Trim();
        if (v.Length == 0 || v == Model.Name) { Name = Model.Name; return; }
        _owner.Update(this, new Dictionary<string, object?> { ["name"] = v });
    }

    public void CommitEmoji()
    {
        var v = Emoji.Trim();
        if (v == (Model.Emoji ?? "")) return;
        _owner.Update(this, new Dictionary<string, object?> { ["emoji"] = v.Length == 0 ? null : v });
    }

    public void CommitPath()
    {
        var v = Path.Trim();
        if (v == (Model.Path ?? "")) return;
        _owner.Update(this, new Dictionary<string, object?> { ["path"] = v.Length == 0 ? null : v });
    }

    public void CommitDescription()
    {
        var v = Description.Trim();
        if (v == (Model.Description ?? "")) return;
        _owner.Update(this, new Dictionary<string, object?> { ["description"] = v.Length == 0 ? null : v });
    }

    /// <summary>Esc: 編集を取り消して元に戻す。</summary>
    public void Revert() => Load(Model);

    [RelayCommand]
    private void SetColor(string? color)
    {
        ColorOpen = false;
        if (string.IsNullOrWhiteSpace(color) || color == Model.Color) return;
        _owner.Update(this, new Dictionary<string, object?> { ["color"] = color });
    }

    public void CommitHex()
    {
        var v = HexInput.Trim();
        if (TagsAdminViewModel.IsHex(v)) SetColor(v.ToLowerInvariant());
        else HexInput = Model.Color;
    }

    [RelayCommand]
    private void ToggleFavorite()
        => _owner.Update(this, new Dictionary<string, object?> { ["favorite"] = !Model.Favorite });

    [RelayCommand]
    private void ToggleArchive()
        => _owner.Update(this, new Dictionary<string, object?> { ["archived"] = !Model.Archived });

    [RelayCommand]
    private void Delete() => _owner.Delete(this);
}

/// <summary>Next.js 版 TagsAdmin 相当のタグ管理画面。</summary>
public sealed partial class TagsAdminViewModel : ObservableObject
{
    private readonly KanbanService _svc;
    private readonly Action<string> _notify;
    private readonly Dictionary<Guid, TagRowVm> _rows = new();

    public TagsAdminViewModel(KanbanService svc, Action<string> notify)
    {
        _svc = svc;
        _notify = notify;
    }

    public ObservableCollection<TagRowVm> WorkTags { get; } = new();
    public ObservableCollection<TagRowVm> ProjectTags { get; } = new();
    public ObservableCollection<TagRowVm> OtherTags { get; } = new();
    public ObservableCollection<TagRowVm> ArchivedTags { get; } = new();
    public ObservableCollection<ParentOptionVm> ParentOptions { get; } = new();
    public IReadOnlyList<string> Presets => TagPalette.Colors;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _filteredCount;

    // 新規タグ
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewName), nameof(PreviewColor), nameof(NewColorLabel), nameof(HasPreview))]
    private string _newName = "";
    [ObservableProperty] private string _newEmoji = "";
    [ObservableProperty] private string _newPath = "";
    [ObservableProperty] private string _newDescription = "";
    /// <summary>null は Auto（タグ名から自動配色）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewColor), nameof(NewColorLabel), nameof(NewColorIsAuto))]
    private string? _newColor;
    [ObservableProperty] private string _newHexInput = "";
    [ObservableProperty] private bool _newColorOpen;

    public bool HasPreview => NewName.Trim().Length > 0;
    public string PreviewName => NewName.Trim();
    public string PreviewColor => NewColor ?? TagPalette.Pick(NewName.Trim());
    public bool NewColorIsAuto => NewColor is null;
    public string NewColorLabel => NewColor is null ? "Auto" : NewColor;

    public bool HasWork => WorkTags.Count > 0;
    public bool HasProject => ProjectTags.Count > 0;
    public bool HasOther => OtherTags.Count > 0;
    public bool HasArchived => ArchivedTags.Count > 0;
    public bool IsEmpty => FilteredCount == 0;
    public string EmptyText => TotalCount == 0 ? "まだタグがありません" : "一致するタグがありません";

    partial void OnFilterChanged(string value) => Sync();

    public static bool IsHex(string s)
        => System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9a-fA-F]{6}$");

    /// <summary>サービスの内容と同期。行 VM は id で使い回し、編集中の行は上書きしない。</summary>
    public void Sync()
    {
        var all = _svc.ListTags(includeArchived: true).Select(t => t.Tag).ToList();
        TotalCount = all.Count;

        // 親タグの選択肢（カテゴリタグ）
        var parents = all.Where(t => !t.Archived && TagRules.IsCategory(t.Name))
            .OrderBy(t => TagRules.IsWork(t.Name) ? 0 : 1).ThenBy(t => t.Name).ToList();
        var parentKey = string.Join("|", parents.Select(p => p.Id + p.Name));
        if (parentKey != string.Join("|", ParentOptions.Skip(1).Select(p => p.Id + p.Name)) || ParentOptions.Count == 0)
        {
            ParentOptions.Clear();
            ParentOptions.Add(new ParentOptionVm { Id = null, Name = "— なし (全アイテムで候補)" });
            foreach (var p in parents) ParentOptions.Add(new ParentOptionVm { Id = p.Id, Name = p.Name });
        }

        var ids = all.Select(t => t.Id).ToHashSet();
        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList()) _rows.Remove(gone);
        foreach (var t in all)
        {
            if (_rows.TryGetValue(t.Id, out var row))
            {
                if (!row.IsDirty) row.Load(t);
            }
            else _rows[t.Id] = new TagRowVm(this, t);
        }

        var f = Filter.Trim();
        var visible = all.Where(t => f.Length == 0 || t.Name.Contains(f, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Favorite).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => _rows[t.Id]).ToList();
        FilteredCount = visible.Count;

        SyncGroup(WorkTags, visible.Where(r => !r.Model.Archived && TagRules.IsWork(r.Model.Name)));
        SyncGroup(ProjectTags, visible.Where(r => !r.Model.Archived && TagRules.IsProject(r.Model.Name)));
        SyncGroup(OtherTags, visible.Where(r => !r.Model.Archived && !TagRules.IsCategory(r.Model.Name)));
        SyncGroup(ArchivedTags, visible.Where(r => r.Model.Archived));
        OnPropertyChanged(nameof(HasWork));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(HasOther));
        OnPropertyChanged(nameof(HasArchived));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    private static void SyncGroup(ObservableCollection<TagRowVm> target, IEnumerable<TagRowVm> rows)
    {
        var list = rows.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var r in list) target.Add(r);
    }

    public void Update(TagRowVm row, Dictionary<string, object?> patch)
    {
        try
        {
            var tag = _svc.UpdateTag(row.Id, JsonSerializer.SerializeToElement(patch), "user");
            row.Load(tag);
        }
        catch (KanbanException e)
        {
            row.Revert();
            _notify(e.Message);
        }
        Sync();
    }

    public void Delete(TagRowVm row)
    {
        if (MessageBox.Show($"タグ「{row.Model.Name}」を削除しますか？（全アイテムから外れます）", "Kanban",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _svc.DeleteTag(row.Id, "user");
        _notify($"タグ「{row.Model.Name}」を削除しました");
        Sync();
    }

    [RelayCommand]
    private void Create()
    {
        var name = NewName.Trim();
        if (name.Length == 0) return;
        if (_svc.ListTags(includeArchived: true).Any(t => t.Tag.Name == name))
        {
            _notify($"タグ「{name}」は既にあります");
            return;
        }
        try
        {
            _svc.CreateTag(new TagCreate(name,
                Color: NewColor,
                Path: NullIfEmpty(NewPath),
                Description: NullIfEmpty(NewDescription),
                Emoji: NullIfEmpty(NewEmoji)), "user");
        }
        catch (KanbanException e)
        {
            _notify(e.Message);
            return;
        }
        _notify($"タグ「{name}」を作成しました");
        NewName = NewEmoji = NewPath = NewDescription = "";
        NewColor = null;
        Sync();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [RelayCommand]
    private void SetNewColor(string? color)
    {
        NewColorOpen = false;
        NewColor = string.IsNullOrWhiteSpace(color) ? null : color;
        NewHexInput = NewColor ?? "";
    }

    public void CommitNewHex()
    {
        var v = NewHexInput.Trim();
        if (IsHex(v)) SetNewColor(v.ToLowerInvariant());
        else NewHexInput = NewColor ?? "";
    }

    [RelayCommand]
    private void ResetAllColors()
    {
        var tags = _svc.ListTags(includeArchived: true).Select(t => t.Tag).ToList();
        if (MessageBox.Show($"全タグ {tags.Count} 件の色を、タグ名から自動算出された色で上書きします。よろしいですか？",
                "Kanban", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var n = 0;
        foreach (var t in tags)
        {
            var next = TagPalette.Pick(t.Name);
            if (string.Equals(t.Color, next, StringComparison.OrdinalIgnoreCase)) continue;
            _svc.UpdateTag(t.Id, JsonSerializer.SerializeToElement(new { color = next }), "user");
            n++;
        }
        _notify($"{n} 件のタグの色を更新しました");
        Sync();
    }
}
