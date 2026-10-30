using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Kanban.Desktop.ViewModels;

namespace Kanban.Desktop;

public partial class MainWindow : Window
{
    private Point _dragStart;
    private Point _grabOffset;
    private ItemCardVm? _dragCard;
    private FrameworkElement? _dragCardElement;
    private ItemCardVm? _indicatorCard;
    private bool _dragged;
    private double _dragHeight;
    private DragGhostAdorner? _ghost;
    private BoardVm? _targetBoard;
    private ItemCardVm? _targetBefore;
    private bool _targetIsOrigin;
    private (ItemCardVm Card, BoardVm Board, int Index)? _pendingMove;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_LBUTTON = 0x01;

    private const int VK_ESCAPE = 0x1B;
    private bool _backdropPressed;
    private long _lastLoadMoreTick;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.PropertyChanged += Vm_PropertyChanged;
        };
        ApplyDetailWidth();
        UiZoom.Changed += () => (DataContext as MainViewModel)?.RefreshSettingsTexts();
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.QuickAdd))
        {
            ApplyDetailWidth();
            // 開いたら表示後にタイトル欄へフォーカス
            if (Vm.QuickAdd is not null)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => QuickAddTitleBox.Focus());
            return;
        }
        if (e.PropertyName != nameof(MainViewModel.Detail)) return;

        if (Vm.Detail is null)
        {
            // 閉じたら詳細内の入力欄からフォーカスを外し、ボード側へ戻す
            Keyboard.ClearFocus();
            Keyboard.Focus(BoardsScroll);
            return;
        }
        DetailScroll.ScrollToTop();
    }

    // ---- view switching ----

    private void ShowBoards_Click(object sender, RoutedEventArgs e) => Vm.SwitchToBoards();

    private void ShowActivity_Click(object sender, RoutedEventArgs e) => Vm.ShowActivity();

    private void ShowTags_Click(object sender, RoutedEventArgs e) => Vm.ShowTags();

    // ---- Tags 画面 ----

    private static void CommitTagField(TextBox box)
    {
        if (box.DataContext is not TagRowVm row) return;
        // Tag はプレースホルダー文字に使っているので、欄の識別は AutomationProperties.Name で行う
        switch (System.Windows.Automation.AutomationProperties.GetName(box))
        {
            case "name": row.CommitName(); break;
            case "emoji": row.CommitEmoji(); break;
            case "path": row.CommitPath(); break;
            case "desc": row.CommitDescription(); break;
            case "hex": row.CommitHex(); break;
        }
    }

    private void TagField_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) CommitTagField(box);
    }

    private void TagField_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not TagRowVm row) return;
        if (e.Key == Key.Enter)
        {
            CommitTagField(box);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            row.Revert();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void NewTagName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm.TagsAdmin.CreateCommand.Execute(null);
            NewTagNameBox.Focus();
            e.Handled = true;
        }
    }

    private void NewHex_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => Vm.TagsAdmin.CommitNewHex();

    private void NewHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm.TagsAdmin.CommitNewHex();
            e.Handled = true;
        }
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e) => CloseDetail();

    private void CloseDetail()
    {
        // フォーカス中の入力欄の内容をバインディングに確定させてから閉じる（自動保存）
        Keyboard.ClearFocus();
        Vm.CloseDetail();
    }

    // ---- sidebar ----

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e) => Vm.SidebarCollapsed = !Vm.SidebarCollapsed;

    // ---- 複数選択バー ----

    private void SelectionMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry("番号をまとめてコピー", (Geometry)FindResource("CopyIcon"), null, Vm.CopySelectedNumbers));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuSeparator") });
        menu.Items.Add(MenuEntry("選択を解除", (Geometry)FindResource("CloseIcon"), null, Vm.ClearSelection));
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => Vm.ClearSelection();

    /// <summary>
    /// 複数選択は、選択に関係のない操作（Ctrl なしのクリックなど）をしたら解除する。
    /// 残すのは Ctrl+クリック（選択の追加・解除）、選択バーの操作、選択中のカードの右クリック（まとめてコピーのメニュー）だけ。
    /// </summary>
    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Vm.HasSelection || (Keyboard.Modifiers & ModifierKeys.Control) != 0) return;
        var src = e.OriginalSource as DependencyObject;
        if (IsInside(src, SelectionBar)) return;
        var context = src switch
        {
            FrameworkElement fe => fe.DataContext,
            FrameworkContentElement fce => fce.DataContext, // カード内の Run など
            _ => null,
        };
        if (e.ChangedButton == MouseButton.Right && context is ItemCardVm card && Vm.IsCardSelected(card)) return;
        Vm.ClearSelection();
    }

    private static bool IsInside(DependencyObject? d, DependencyObject root)
    {
        while (d is not null)
        {
            if (ReferenceEquals(d, root)) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void SidebarAll_Click(object sender, RoutedEventArgs e) => Vm.SelectCategoryTag(null, false);

    private void SidebarToday_Click(object sender, RoutedEventArgs e) => Vm.SelectToday();

    private void SidebarTag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SidebarTagVm entry)
            Vm.SelectCategoryTag(entry, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    private void SidebarSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Vm.SidebarQuery = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Vm.SelectFirstSidebarMatch();
            e.Handled = true;
        }
    }

    private void SidebarSearchClear_Click(object sender, RoutedEventArgs e)
    {
        Vm.SidebarQuery = "";
        SidebarSearchBox.Focus();
    }

    // ---- board settings ----

    private void BoardSettings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BoardVm board)
            OpenBoardSettings(board.Model.Id, focusNew: false);
    }

    private void AddBoard_Click(object sender, RoutedEventArgs e) => OpenBoardSettings(null, focusNew: true);

    private void OpenBoardSettings(Guid? boardId, bool focusNew)
    {
        Vm.OpenBoardSettings(boardId);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (focusNew) { NewBoardNameBox.Focus(); return; }
            // 開いたボードの名前欄にフォーカス
            var box = FindVisualChildren<TextBox>(BoardSettingsOverlay)
                .FirstOrDefault(t => t.DataContext is BoardRowVm r && r.Id == boardId
                                     && System.Windows.Automation.AutomationProperties.GetName(t) == "name");
            box?.Focus();
            box?.SelectAll();
        });
    }

    private void CloseBoardSettings_Click(object sender, RoutedEventArgs e) => CloseBoardSettings();

    private void CloseBoardSettings()
    {
        // フォーカス中の欄の編集を確定してから閉じる
        Keyboard.ClearFocus();
        Vm.CloseBoardSettings();
    }

    private void BoardSettingsBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _backdropPressed = ReferenceEquals(e.OriginalSource, BoardSettingsOverlay);
        if (_backdropPressed) e.Handled = true;
    }

    private void BoardSettingsBackdrop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var close = _backdropPressed && ReferenceEquals(e.OriginalSource, BoardSettingsOverlay);
        _backdropPressed = false;
        if (close)
        {
            e.Handled = true;
            CloseBoardSettings();
        }
    }

    private static void CommitBoardField(TextBox box)
    {
        if (box.DataContext is not BoardRowVm row) return;
        switch (System.Windows.Automation.AutomationProperties.GetName(box))
        {
            case "name": row.CommitName(); break;
            case "emoji": row.CommitEmoji(); break;
            case "hex": row.CommitHex(); break;
        }
    }

    private void BoardField_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox box) CommitBoardField(box);
    }

    private void BoardField_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not BoardRowVm row) return;
        if (e.Key == Key.Enter)
        {
            CommitBoardField(box);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && row.IsDirty)
        {
            // 編集中なら取り消し（Esc は閉じる操作に回さない）
            row.Revert();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void NewBoard_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm.BoardSettings is { } settings)
        {
            settings.CreateCommand.Execute(null);
            NewBoardNameBox.Focus();
            e.Handled = true;
        }
    }

    // ---- search ----

    private void Search_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Vm.SearchText = "";
            e.Handled = true;
        }
    }

    private void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        Vm.SearchText = "";
        SearchBox.Focus();
    }

    // ---- window-level keys ----

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 文字サイズ: Ctrl+; (JIS の ;+ キー) / Ctrl+- / Ctrl+0。どの画面でも効く
        if (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (e.RealKey())
            {
                case Key.OemPlus or Key.Add:
                    UiZoom.Set(UiZoom.Scale + UiZoom.Step); e.Handled = true; return;
                case Key.OemMinus or Key.Subtract:
                    UiZoom.Set(UiZoom.Scale - UiZoom.Step); e.Handled = true; return;
                case Key.D0 or Key.NumPad0 when Keyboard.Modifiers == ModifierKeys.Control:
                    UiZoom.Set(1.0); e.Handled = true; return;
            }
        }
        if (Vm.IsSettingsOpen)
        {
            if (e.Key == Key.Escape)
            {
                Vm.IsSettingsOpen = false;
                e.Handled = true;
            }
            return;
        }
        if (Vm.BoardSettings is not null)
        {
            if (e.Key == Key.Escape
                && !(Keyboard.FocusedElement is TextBox { DataContext: BoardRowVm r } && r.IsDirty))
            {
                CloseBoardSettings();
                e.Handled = true;
            }
            return;
        }
        if (Vm.Detail is not null)
        {
            // モーダル表示中: Esc で閉じる（自動保存）。検索ショートカットは無効
            // 追加フォームに入力中の Esc はフォームだけを閉じる。Ctrl+N で追加フォームを開閉
            if (e.Key == Key.Escape)
            {
                if (Vm.QuickAdd is not null && QuickAddPanel.IsKeyboardFocusWithin)
                    CloseQuickAdd();
                else
                    CloseDetail();
                e.Handled = true;
            }
            else if (e.RealKey() == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (Vm.QuickAdd is not null && !QuickAddPanel.IsKeyboardFocusWithin)
                    QuickAddTitleBox.Focus();
                else
                    ToggleQuickAdd();
                e.Handled = true;
            }
            return;
        }
        // カードの複数選択中: Esc で解除、Ctrl+C で番号をまとめてコピー（入力欄にフォーカスがあるときは通常のコピー）
        if (Vm.HasSelection && Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase)
        {
            if (e.Key == Key.Escape)
            {
                Vm.ClearSelection();
                e.Handled = true;
                return;
            }
            if (e.RealKey() == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Vm.CopySelectedNumbers();
                e.Handled = true;
                return;
            }
        }
        if (e.RealKey() == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // ---- inline add ----

    private void BeginAdd_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not BoardVm board) return;
        board.IsAdding = true;
        // 可視化されたあとに入力欄へフォーカス
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (fe.Parent is Grid grid)
                grid.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
        });
    }

    private void NewItem_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not BoardVm board) return;
        if (e.Key == Key.Enter)
        {
            var title = board.NewItemText;
            board.NewItemText = "";
            Vm.AddItem(board, title);
            // Reload でカラムが作り直されるので、新しい入力欄を開き直して連続追加できるようにする
            ReopenAdd(board.Model.Id);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            board.NewItemText = "";
            board.IsAdding = false;
            e.Handled = true;
        }
    }

    private void ReopenAdd(Guid boardId)
    {
        var board = Vm.Boards.FirstOrDefault(b => b.Model.Id == boardId);
        if (board is null) return;
        board.IsAdding = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            var box = FindVisualChildren<TextBox>(BoardsScroll)
                .FirstOrDefault(t => ReferenceEquals(t.DataContext, board) && t.IsVisible);
            box?.Focus();
        });
    }

    // ---- paging (Done) ----

    private void BoardList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BoardVm board } || !board.HasMore) return;
        // 下方向にスクロールして一番下（の少し手前）まで来たら続きを取得。
        // 取得で中身が伸びただけの通知（VerticalChange == 0）では連鎖させない
        if (e.VerticalChange > 0 && e.ExtentHeight > 0
            && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 48
            && Environment.TickCount64 - _lastLoadMoreTick > 500)
        {
            // 仮想化パネルの高さ見積もり補正でスクロール位置が末尾に寄り直されることがあるため、
            // 連続取得しないよう 0.5 秒は間引く
            _lastLoadMoreTick = Environment.TickCount64;
            Vm.LoadMore(board);
        }
    }

    private void LoadMore_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BoardVm board)
            Vm.LoadMore(board);
    }

    private void NewItem_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BoardVm board
            && string.IsNullOrWhiteSpace(board.NewItemText))
            board.IsAdding = false;
    }

    // ---- drag & drop (Trello 風: つかんだカードがカーソルに付いてきて、落とし先に隙間が開く) ----

    private void Card_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragged = false;
        _dragCard = (sender as FrameworkElement)?.DataContext as ItemCardVm;
        _dragCardElement = sender as FrameworkElement;
        _grabOffset = _dragCardElement is null ? default : e.GetPosition(_dragCardElement);
    }

    private void Card_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCard is null || _dragged
            || _dragCardElement is null) return;
        RemoveGhost(); // 念のため前回の残りを掃除
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < 5 && Math.Abs(pos.Y - _dragStart.Y) < 5) return;
        _dragged = true;
        var card = _dragCard;
        var element = _dragCardElement;
        _dragHeight = Math.Max(36, element.ActualHeight);

        // 元カードを隠す前にスナップショットを取ってゴーストにする
        var layer = AdornerLayer.GetAdornerLayer(RootGrid);
        if (layer is not null)
        {
            _ghost = new DragGhostAdorner(RootGrid, element, _grabOffset);
            layer.Add(_ghost);
            _ghost.MoveTo(e.GetPosition(RootGrid));
        }
        card.Dragging = true;
        Vm.IsDragging = true;
        _pendingMove = null;
        element.GiveFeedback += Card_GiveFeedback;
        element.QueryContinueDrag += Card_QueryContinueDrag;
        try
        {
            DragDrop.DoDragDrop(element, card, DragDropEffects.Move);
        }
        finally
        {
            element.GiveFeedback -= Card_GiveFeedback;
            element.QueryContinueDrag -= Card_QueryContinueDrag;
            card.Dragging = false;
            RemoveGhost();
            ClearTarget();
            _dragCard = null;
            _dragCardElement = null;
            Vm.IsDragging = false;
        }

        // 移動はドラッグ操作が完全に終わってから行う（ドロップ処理中に一覧を作り直さない）
        if (_pendingMove is { } m)
        {
            _pendingMove = null;
            try { Vm.MoveItem(m.Card, m.Board, m.Index); }
            catch (Exception ex) { Vm.ShowNotice("移動できませんでした: " + ex.Message); }
        }
    }

    private void RemoveGhost()
    {
        if (_ghost is null) return;
        AdornerLayer.GetAdornerLayer(RootGrid)?.Remove(_ghost);
        _ghost = null;
    }

    private void Card_QueryContinueDrag(object sender, QueryContinueDragEventArgs e)
    {
        // マウスボタンの状態を OS から直接見て、離されていれば必ず終わらせる
        // （ボタンを離したイベントを取りこぼしてドラッグが終わらなくなるのを防ぐ）
        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0 || e.EscapePressed)
        {
            e.Action = DragAction.Cancel;
            e.Handled = true;
        }
        else if ((GetAsyncKeyState(SystemParameters.SwapButtons ? 0x02 : VK_LBUTTON) & 0x8000) == 0)
        {
            e.Action = DragAction.Drop;
            e.Handled = true;
        }
    }

    private void Card_GiveFeedback(object sender, GiveFeedbackEventArgs e)
    {
        // OS 既定の「禁止」マーク等は出さず、つかんでいる手のカーソルにする
        e.UseDefaultCursors = false;
        Mouse.SetCursor(Cursors.Hand);
        e.Handled = true;
    }

    private void Root_PreviewDragOver(object sender, DragEventArgs e)
    {
        _ghost?.MoveTo(e.GetPosition(RootGrid));
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        // カラムの外: 落とし先スロットはそのまま（離したらそこへ入れる。Trello と同じ）
        e.Effects = _targetBoard is null ? DragDropEffects.None : DragDropEffects.Move;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        CommitDrop();
        e.Handled = true;
    }

    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 同じカード上で押して離したときだけ開く（モーダルを閉じた直後の「離す」だけでは開かない）
        // 押してから離すまでの間に一覧が作り直されてもカードの VM が差し替わるだけなので、アイテム ID で比べる
        // Ctrl+クリックは開かずに選択の切り替え（複数選択）
        if (!_dragged && (sender as FrameworkElement)?.DataContext is ItemCardVm card
            && _dragCard is not null && card.Item.Id == _dragCard.Item.Id)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) Vm.ToggleCardSelection(card);
            else Vm.OpenItem(card);
        }
        _dragCard = null;
        _dragCardElement = null;
        _dragged = false;
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (_dragCard is null || sender is not FrameworkElement area
            || area.DataContext is not ItemCardVm hovered || area.Tag is not FrameworkElement cardEl) return;
        var board = Vm.Boards.FirstOrDefault(b => b.Items.Contains(hovered));
        if (board is null) return; // 検索結果などボード外
        e.Effects = DragDropEffects.Move;

        var y = e.GetPosition(cardEl).Y;
        // 開いているスロットの上にいる間は位置を変えない（押しのけたカードとの行き来でちらつかないように）
        if (y < 0 && hovered.DropIndicator == "top") return;
        if (y > cardEl.ActualHeight + 8 && hovered.DropIndicator == "bottom") return;

        var index = board.Items.IndexOf(hovered) + (y >= cardEl.ActualHeight / 2 ? 1 : 0);
        SetTarget(board, index);
    }

    private void Column_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_dragCard is null || (sender as FrameworkElement)?.DataContext is not BoardVm board)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DragDropEffects.Move;
        // ボード名・「+ アイテムを追加」のあたり（カード一覧より上）なら先頭へ。
        // カード一覧の空き部分（最後のカードより下）なら末尾へ
        var inList = e.OriginalSource is DependencyObject src && FindAncestor<ListBox>(src) is not null;
        SetTarget(board, inList ? board.Items.Count : 0);
    }

    private void Column_DragEnter(object sender, DragEventArgs e) => Column_DragOver(sender, e);

    private void Column_DragLeave(object sender, DragEventArgs e) { }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        CommitDrop();
        e.Handled = true;
    }

    private void Column_Drop(object sender, DragEventArgs e)
    {
        CommitDrop();
        e.Handled = true;
    }

    /// <summary>
    /// 表示中リストの index の「前」に入れる、という形で落とし先を決める。
    /// 1 つの隙間は常に「次のカードの上」（末尾なら最後のカードの下）で表すので、
    /// 上下どちらのカードから来ても同じスロットになりちらつかない。
    /// </summary>
    private void SetTarget(BoardVm board, int index)
    {
        var list = board.Items;
        var from = list.IndexOf(_dragCard!);
        if (from >= 0 && (index == from || index == from + 1))
        {
            ApplyTarget(board, null, origin: true); // 元の位置のまま
            return;
        }
        ApplyTarget(board, index < list.Count ? list[index] : null, origin: false);
    }

    private void ApplyTarget(BoardVm board, ItemCardVm? before, bool origin)
    {
        ItemCardVm? slotCard = null;
        string? where = null;
        if (!origin)
        {
            if (before is not null) { slotCard = before; where = "top"; }
            else
            {
                slotCard = board.Items.LastOrDefault(c => !ReferenceEquals(c, _dragCard));
                where = "bottom";
            }
        }
        if (!ReferenceEquals(_indicatorCard, slotCard) && _indicatorCard is not null)
            _indicatorCard.DropIndicator = null;
        _indicatorCard = slotCard;
        if (slotCard is not null)
        {
            slotCard.SlotHeight = _dragHeight;
            slotCard.DropIndicator = where;
        }
        foreach (var b in Vm.Boards) b.IsDropTarget = ReferenceEquals(b, board) && !origin;
        _targetBoard = board;
        _targetBefore = before;
        _targetIsOrigin = origin;
    }

    private void ClearTarget()
    {
        if (_indicatorCard is not null) _indicatorCard.DropIndicator = null;
        _indicatorCard = null;
        foreach (var b in Vm.Boards) b.IsDropTarget = false;
        _targetBoard = null;
        _targetBefore = null;
        _targetIsOrigin = false;
    }

    private void CommitDrop()
    {
        var dragged = _dragCard;
        var board = _targetBoard;
        var before = _targetBefore;
        var origin = _targetIsOrigin;
        ClearTarget();
        if (dragged is null || board is null || origin) return;

        // 絞り込み中でも正しい位置に入るよう、全体（SourceItems）上の index で計算する。
        // ページング中のボードの末尾は、表示中の最後のカードの直後（本当の末尾に入れると未取得分に埋もれて消えたように見える）
        var all = board.SourceItems;
        var index = before is not null ? all.IndexOf(before) : all.Count;
        if (index < 0) return;
        var fromIndex = all.IndexOf(dragged);
        if (fromIndex >= 0 && fromIndex < index) index--;
        if (fromIndex == index) return;
        _pendingMove = (dragged, board, index);
    }

    // ---- card context menu (Next.js 版 TagContextMenu 相当) ----

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ItemCardVm card) return;
        var menu = BuildCardMenu(card);
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private ContextMenu BuildCardMenu(ItemCardVm card)
    {
        var menu = new ContextMenu();
        var sep = (Style)FindResource("MenuSeparator");
        var header = (Style)FindResource("MenuHeader");

        // 複数選択中のカードならまとめてコピーだけ、それ以外はそのカードの番号のコピーだけ
        if (Vm.IsCardSelected(card) && Vm.SelectedCount > 1)
            menu.Items.Add(MenuEntry($"選択中の {Vm.SelectedCount} 件の番号をコピー", (Geometry)FindResource("CopyIcon"), null,
                Vm.CopySelectedNumbers));
        else
            menu.Items.Add(MenuEntry("番号をコピー", (Geometry)FindResource("CopyIcon"), null,
                () => Vm.CopyCardNumber(card)));
        menu.Items.Add(new Separator { Style = sep });

        menu.Items.Add(new MenuItem { Header = "期限", Style = header });
        var due = card.Item.DueDate;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        menu.Items.Add(MenuEntry("今日", null, due == today ? "#F97316" : null, () => Vm.SetCardDue(card, "today")));
        menu.Items.Add(MenuEntry("明日", null, due == tomorrow ? "#FB923C" : null, () => Vm.SetCardDue(card, "tomorrow")));
        menu.Items.Add(MenuEntry("1 週間後", null, null, () => Vm.SetCardDue(card, "week")));
        if (!string.IsNullOrEmpty(due))
            menu.Items.Add(MenuEntry("期限をクリア", null, null, () => Vm.SetCardDue(card, "clear")));
        menu.Items.Add(new Separator { Style = sep });

        menu.Items.Add(new MenuItem { Header = "タグ（クリックで付け外し）", Style = header });
        foreach (var t in Vm.TagMenuFor(card))
        {
            var tag = t;
            menu.Items.Add(TagEntry(tag, () => Vm.ToggleCardTag(card, tag.Tag, tag.Attached)));
        }
        return menu;
    }

    private static MenuItem MenuEntry(string text, Geometry? icon, string? activeColor, Action onClick)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon is not null)
            panel.Children.Add(new Path
            {
                Data = icon, Stroke = new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x73)), StrokeThickness = 1.5,
                Stretch = Stretch.Uniform, Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        panel.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        if (activeColor is not null)
            panel.Children.Add(new Ellipse
            {
                Width = 7, Height = 7, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)new BrushConverter().ConvertFromString(activeColor)!,
            });
        var item = new MenuItem { Header = panel };
        item.Click += (_, _) => onClick();
        return item;
    }

    private static MenuItem TagEntry(TagMenuVm tag, Action onClick)
    {
        var grid = new Grid { MinWidth = 190 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Brush color;
        try { color = (Brush)new BrushConverter().ConvertFromString(tag.Color)!; }
        catch { color = Brushes.SlateGray; }
        grid.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = color, HorizontalAlignment = HorizontalAlignment.Left });
        var name = new TextBlock { Text = tag.Name, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        if (tag.Attached)
        {
            var check = new TextBlock
            {
                Text = "✓", Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(check, 2);
            grid.Children.Add(check);
        }
        var item = new MenuItem { Header = grid };
        item.Click += (_, _) => onClick();
        return item;
    }

    // ---- detail modal ----

    private void DetailBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _backdropPressed = ReferenceEquals(e.OriginalSource, DetailOverlay);
        e.Handled = true;
    }

    private void DetailBackdrop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // 押した位置も離した位置も暗い背景のときだけ閉じる。
        // 押した瞬間に閉じると、離したクリックが下のカードに届いて別のアイテムが開いてしまうため
        var close = _backdropPressed && ReferenceEquals(e.OriginalSource, DetailOverlay);
        _backdropPressed = false;
        e.Handled = true;
        if (close) CloseDetail();
    }

    private void DetailCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // モーダル内の余白クリックで入力欄からフォーカスを外す（=自動保存）
        if (e.OriginalSource is DependencyObject src && FindAncestor<TextBox>(src) is null
            && Keyboard.FocusedElement is TextBox)
        {
            Keyboard.ClearFocus();
            DetailCard.Focusable = true;
            DetailCard.Focus();
        }
    }

    // ---- 詳細の幅（左右の端をドラッグ。ui.json に保存） ----

    private const double DetailMinWidth = 480;

    /// <summary>詳細（+ 開いていれば追加フォーム）の最大幅を保存済みの幅に合わせる（width はスクショ用の上書き）。</summary>
    internal void ApplyDetailWidth(double? width = null)
    {
        var quickAdd = DataContext is MainViewModel { QuickAdd: not null } ? QuickAddPanel.Width + QuickAddPanel.Margin.Left : 0;
        DetailHost.MaxWidth = (width ?? UiState.Current.DetailWidthOrDefault) + quickAdd;
    }

    private void DetailResize_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // 中央寄せなので片側を dx 動かすと両側に dx ずつ広がる（= 幅は 2dx）。つまみはカーソルの下に残る
        var dx = (sender as FrameworkElement)?.Tag as string == "left" ? -e.HorizontalChange : e.HorizontalChange;
        // 窓より広くはできない（広げても表示が変わらないまま値だけ増えないように、表示できる幅で頭打ち）
        var quickAdd = Vm.QuickAdd is not null ? QuickAddPanel.Width + QuickAddPanel.Margin.Left : 0;
        var available = DetailOverlay.ActualWidth - DetailHost.Margin.Left - DetailHost.Margin.Right - quickAdd;
        var width = Math.Clamp(DetailCardHost.ActualWidth + dx * 2, DetailMinWidth, Math.Max(DetailMinWidth, available));
        UiState.Current.DetailWidth = Math.Round(width);
        ApplyDetailWidth();
    }

    private void DetailResize_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        UiState.Current.Save();
        Vm.RefreshSettingsTexts();
    }

    private void DetailResize_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        ResetDetailWidth();
        e.Handled = true;
    }

    private void ResetDetailWidth()
    {
        UiState.Current.DetailWidth = null;
        UiState.Current.Save();
        ApplyDetailWidth();
        Vm.RefreshSettingsTexts();
    }

    // ---- 設定（文字サイズ・詳細の幅） ----

    private void ShowSettings_Click(object sender, RoutedEventArgs e)
    {
        Vm.RefreshSettingsTexts();
        Vm.IsSettingsOpen = true;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => Vm.IsSettingsOpen = false;

    private void SettingsBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _backdropPressed = ReferenceEquals(e.OriginalSource, SettingsOverlay);
        if (_backdropPressed) e.Handled = true;
    }

    private void SettingsBackdrop_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var close = _backdropPressed && ReferenceEquals(e.OriginalSource, SettingsOverlay);
        _backdropPressed = false;
        if (close)
        {
            e.Handled = true;
            Vm.IsSettingsOpen = false;
        }
    }

    private void UiScalePreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string s
            && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale))
            UiZoom.Set(scale);
    }

    private void UiScaleStep_Click(object sender, RoutedEventArgs e) =>
        UiZoom.Set(UiZoom.Scale + ((sender as FrameworkElement)?.Tag as string == "-" ? -UiZoom.Step : UiZoom.Step));

    private void ResetDetailWidth_Click(object sender, RoutedEventArgs e) => ResetDetailWidth();

    /// <summary>Ctrl + ホイールで文字サイズを変える（スクロールより先に拾う）。</summary>
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        UiZoom.Set(UiZoom.Scale + (e.Delta > 0 ? UiZoom.Step : -UiZoom.Step));
        e.Handled = true;
    }

    private void AutoSave_LostFocus(object sender, RoutedEventArgs e) => Vm.Detail?.SaveIfDirty();

    // ---- 詳細の右の追加フォーム ----

    private void ToggleQuickAdd_Click(object sender, RoutedEventArgs e) => ToggleQuickAdd();

    private void ToggleQuickAdd()
    {
        if (Vm.QuickAdd is not null) CloseQuickAdd();
        else Vm.ToggleQuickAdd();
    }

    private void CloseQuickAdd()
    {
        var hadFocus = QuickAddPanel.IsKeyboardFocusWithin;
        Vm.ToggleQuickAdd();
        // フォーム内にあったフォーカスを詳細カードへ戻す（非表示の要素に残さない）
        if (hadFocus)
        {
            DetailCard.Focusable = true;
            DetailCard.Focus();
        }
    }

    private void QuickAddCreate_Click(object sender, RoutedEventArgs e)
    {
        Vm.QuickAdd?.Create();
        QuickAddTitleBox.Focus();
    }

    private void QuickAddTitle_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm.QuickAdd is { } q)
        {
            // 追加したらタイトル欄に戻して続けて入力できるようにする
            if (q.Create()) QuickAddTitleBox.Focus();
            e.Handled = true;
        }
    }

    private void QuickAddTag_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm.QuickAdd is { } q)
        {
            q.AttachTag();
            e.Handled = true;
        }
    }

    private void QuickAddCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // フォーム内の余白クリックで入力欄からフォーカスを外す（詳細カードと同じ挙動）
        if (e.OriginalSource is DependencyObject src && FindAncestor<TextBox>(src) is null
            && Keyboard.FocusedElement is TextBox)
        {
            Keyboard.ClearFocus();
            QuickAddCard.Focusable = true;
            QuickAddCard.Focus();
        }
    }

    private void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm.Detail?.SaveIfDirty();
            e.Handled = true;
        }
    }

    private void DescBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.RealKey() == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Vm.Detail?.SaveIfDirty();
            e.Handled = true;
        }
    }

    private void DueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Vm.Detail?.SaveIfDirty();
            e.Handled = true;
        }
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e) => Vm.DeleteDetailItem();

    private void ToggleSubtask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SubtaskVm sub)
            Vm.Detail?.ToggleSubtask(sub);
    }

    private void DeleteSubtask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SubtaskVm sub)
            Vm.Detail?.DeleteSubtask(sub);
    }

    private void DeleteComment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CommentVm c)
            Vm.Detail?.DeleteComment(c);
    }

    private void CopyComment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommentVm c) return;
        try
        {
            Clipboard.SetText(c.Content);
            Vm.ShowNotice("コメントをコピーしました");
        }
        catch { /* クリップボード競合は無視 */ }
    }

    private void DetachTag_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is TagChipVm chip)
            Vm.Detail?.DetachTag(chip);
    }

    private void NewTag_KeyDown(object sender, KeyEventArgs e)
    {
        if (Vm.Detail is not { } d) return;
        if (e.Key == Key.Enter)
        {
            d.AttachTagCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void NewSubtask_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Vm.Detail is { } d)
        {
            d.AddSubtaskCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void NewComment_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.RealKey() != Key.Enter || Vm.Detail is not { } d) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            d.AddCommentCommand.Execute(null);
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            d.AddCommentAndAssignCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }
}
