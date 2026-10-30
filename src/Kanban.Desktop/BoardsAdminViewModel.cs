using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Kanban.Core;
using Kanban.Core.Models;

namespace Kanban.Desktop.ViewModels;

/// <summary>ボード設定の 1 行。テキスト欄はフォーカスアウト / Enter で確定して保存する。</summary>
public sealed partial class BoardRowVm : ObservableObject
{
    private readonly BoardsAdminViewModel _owner;
    private bool _loading;

    public BoardRowVm(BoardsAdminViewModel owner, Board board, int itemCount)
    {
        _owner = owner;
        Load(board, itemCount);
    }

    public Board Model { get; private set; } = new();
    public Guid Id => Model.Id;
    public string Color => Model.Color;
    public int ItemCount { get; private set; }
    public bool CanMoveUp => _owner.Rows.IndexOf(this) > 0;
    public bool CanMoveDown => _owner.Rows.IndexOf(this) is var i && i >= 0 && i < _owner.Rows.Count - 1;

    // 編集バッファ
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _emoji = "";
    [ObservableProperty] private string _hexInput = "";
    [ObservableProperty] private bool _colorOpen;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private bool _wrapLayout;

    public void Load(Board board, int itemCount)
    {
        _loading = true;
        try
        {
            Model = board;
            ItemCount = itemCount;
            Name = board.Name;
            Emoji = board.Emoji ?? "";
            HexInput = board.Color;
            IsDone = board.IsDone;
            WrapLayout = board.Layout == BoardLayouts.Board;
            OnPropertyChanged(nameof(Color));
            OnPropertyChanged(nameof(ItemCount));
        }
        finally { _loading = false; }
    }

    public void RefreshMoveState()
    {
        OnPropertyChanged(nameof(CanMoveUp));
        OnPropertyChanged(nameof(CanMoveDown));
    }

    public bool IsDirty => Name != Model.Name || Emoji != (Model.Emoji ?? "");

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

    public void CommitHex()
    {
        var v = HexInput.Trim();
        if (TagsAdminViewModel.IsHex(v)) SetColor(v.ToLowerInvariant());
        else HexInput = Model.Color;
    }

    /// <summary>Esc: 編集を取り消して元に戻す。</summary>
    public void Revert() => Load(Model, ItemCount);

    partial void OnIsDoneChanged(bool value)
    {
        if (_loading || value == Model.IsDone) return;
        _owner.Update(this, new Dictionary<string, object?> { ["is_done"] = value });
    }

    partial void OnWrapLayoutChanged(bool value)
    {
        if (_loading) return;
        var layout = value ? BoardLayouts.Board : BoardLayouts.Scroll;
        if (layout == Model.Layout) return;
        _owner.Update(this, new Dictionary<string, object?> { ["layout"] = layout });
    }

    [RelayCommand]
    private void SetColor(string? color)
    {
        ColorOpen = false;
        if (string.IsNullOrWhiteSpace(color) || color == Model.Color) return;
        _owner.Update(this, new Dictionary<string, object?> { ["color"] = color });
    }

    [RelayCommand]
    private void MoveUp() => _owner.Move(this, -1);

    [RelayCommand]
    private void MoveDown() => _owner.Move(this, +1);

    [RelayCommand]
    private void Delete() => _owner.Delete(this);
}

/// <summary>プロジェクト内のボード（列）の追加・名前/絵文字/色・並び順・削除を編集するモーダル。</summary>
public sealed partial class BoardsAdminViewModel : ObservableObject
{
    private readonly KanbanService _svc;
    private readonly Guid _projectId;
    private readonly Action<string> _notify;
    private readonly Dictionary<Guid, BoardRowVm> _rows = new();

    public BoardsAdminViewModel(KanbanService svc, Guid projectId, Action<string> notify)
    {
        _svc = svc;
        _projectId = projectId;
        _notify = notify;
    }

    public ObservableCollection<BoardRowVm> Rows { get; } = new();
    /// <summary>開いた直後に名前欄へフォーカスするボード（ボードヘッダーから開いたとき）。</summary>
    public Guid? FocusBoardId { get; set; }

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newEmoji = "";

    public void Sync()
    {
        var boards = _svc.ListBoards(_projectId);
        var ids = boards.Select(b => b.Id).ToHashSet();
        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList()) _rows.Remove(gone);
        var next = new List<BoardRowVm>(boards.Count);
        foreach (var b in boards)
        {
            var count = _svc.ListItemsForBoard(b.Id, Array.Empty<string>(), false, null, 0).Total;
            if (_rows.TryGetValue(b.Id, out var row))
            {
                if (!row.IsDirty) row.Load(b, count);
            }
            else _rows[b.Id] = row = new BoardRowVm(this, b, count);
            next.Add(row);
        }
        if (!Rows.SequenceEqual(next))
        {
            Rows.Clear();
            foreach (var r in next) Rows.Add(r);
        }
        foreach (var r in Rows) r.RefreshMoveState();
    }

    public void Update(BoardRowVm row, Dictionary<string, object?> patch)
    {
        try
        {
            var board = _svc.UpdateBoard(row.Id, JsonSerializer.SerializeToElement(patch), "user");
            row.Load(board, row.ItemCount);
        }
        catch (KanbanException e)
        {
            row.Revert();
            _notify(e.Message);
        }
        Sync();
    }

    /// <summary>1 つ上 / 下へ。position は 0 から振り直す（変わったボードだけ保存）。</summary>
    public void Move(BoardRowVm row, int delta)
    {
        var order = Rows.ToList();
        var i = order.IndexOf(row);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        for (var p = 0; p < order.Count; p++)
        {
            if (order[p].Model.Position == p) continue;
            _svc.UpdateBoard(order[p].Id, JsonSerializer.SerializeToElement(new { position = p }), "user");
        }
        Sync();
    }

    public void Delete(BoardRowVm row)
    {
        var msg = row.ItemCount > 0
            ? $"ボード「{row.Model.Name}」を削除しますか？\n入っているアイテム {row.ItemCount} 件も削除されます。"
            : $"ボード「{row.Model.Name}」を削除しますか？";
        if (MessageBox.Show(msg, "Kanban", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        _svc.DeleteBoard(row.Id, "user");
        _notify($"ボード「{row.Model.Name}」を削除しました");
        Sync();
    }

    [RelayCommand]
    private void Create()
    {
        var name = NewName.Trim();
        if (name.Length == 0) return;
        try
        {
            var emoji = NewEmoji.Trim();
            _svc.CreateBoard(new BoardCreate(_projectId, name, Emoji: emoji.Length == 0 ? null : emoji), "user");
        }
        catch (KanbanException e)
        {
            _notify(e.Message);
            return;
        }
        _notify($"ボード「{name}」を追加しました");
        NewName = NewEmoji = "";
        Sync();
    }
}
