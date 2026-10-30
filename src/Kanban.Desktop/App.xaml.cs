using System.IO;
using System.Windows;
using System.Windows.Controls;
using Kanban.Api;
using Kanban.Core;
using Kanban.Core.Vault;
using Kanban.Desktop.ViewModels;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Kanban.Desktop;

public partial class App : Application
{
    private KanbanService? _service;
    private VaultWatcher? _watcher;
    private KanbanApiHost? _host;
    private MainWindow? _window;
    private WinForms.NotifyIcon? _tray;
    private bool _exiting;
    private static Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SmoothScroll.Register(); // マウスホイールをなめらかスクロールに
        ImeShortcuts.Register(); // IME オン中でも入力欄の Ctrl+V / C / X / A / Z / Y を効かせる
        PastePath.Register(); // 画像（スクショ）やファイルの貼り付けはパスを入れる

        // オフスクリーンで各画面を描画して PNG に保存する開発用モード。
        // フォアグラウンドを奪わずに UI の見た目を検証できる。常駐中の本体と並行して
        // 動かせるよう、多重起動チェック・API ホスト・UI 状態の保存は行わない。
        if (e.Args.Contains("--screenshot"))
        {
            var dir = e.Args.SkipWhile(a => a != "--screenshot").Skip(1).FirstOrDefault()
                      ?? Path.GetTempPath();
            UiState.Persist = false;
            _service = new KanbanService(AppConfig.Load().VaultRoot);
            _ = RunScreenshotMode(dir);
            return;
        }

        // コメントの簡易 Markdown プレビューを詳細画面と同じ幅で描いて PNG に保存する（開発用）
        //   --mdtest <コメントの .md> <出力 .png>
        if (e.Args.Contains("--mdtest"))
        {
            var rest = e.Args.SkipWhile(a => a != "--mdtest").Skip(1).ToArray();
            UiState.Persist = false;
            _ = RunMarkdownTest(rest[0], rest[1]);
            return;
        }

        // Ctrl+C / Ctrl+V が各入力欄で効くかを本物のキー入力で確かめる（開発用。クリップボードを一時的に使う）
        if (e.Args.Contains("--cliptest"))
        {
            var outDir = e.Args.SkipWhile(a => a != "--cliptest").Skip(1).First();
            UiState.Persist = false;
            _ = RunClipboardTest(outDir);
            return;
        }

        if (e.Args.Contains("--emojitest"))
        {
            var outDir = e.Args.SkipWhile(a => a != "--emojitest").Skip(1).First();
            UiState.Persist = false;
            _ = RunEmojiTest(outDir);
            return;
        }

        // vault のコピーに対して Tags 画面・追加処理を自動検証する開発用モード
        if (e.Args.Contains("--selftest"))
        {
            var root = e.Args.SkipWhile(a => a != "--selftest").Skip(1).First();
            UiState.Persist = false;
            RunSelfTest(root);
            Shutdown();
            return;
        }

        // 開く/閉じる/再描画の所要時間を計測して出力する開発用モード（データは変更しない）
        if (e.Args.Contains("--bench"))
        {
            var outPath = e.Args.SkipWhile(a => a != "--bench").Skip(1).FirstOrDefault()
                          ?? Path.Combine(Path.GetTempPath(), "kanban-bench.txt");
            UiState.Persist = false;
            _ = RunBenchMode(outPath, AppConfig.Load().VaultRoot);
            return;
        }

        _instanceMutex = new Mutex(true, "Kanban.Desktop.SingleInstance", out var isNew);
        if (!isNew)
        {
            // 常駐中のインスタンスにウィンドウ表示を依頼して終了する。
            // トレイアイコンが隠れていても、もう一度起動すれば画面が出るようにするため。
            if (!SignalRunningInstance())
            {
                MessageBox.Show("Kanban は既に起動しています。タスクトレイ（「^」の中）を確認してください。",
                    "Kanban", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            Shutdown();
            return;
        }

        var config = AppConfig.Load();
        var store = new VaultStore(config.VaultRoot);
        _service = new KanbanService(store);
        // 自分の書き込みは無視し、外部（Dropbox 同期・エージェント）の変更だけで全件リロードする
        _watcher = new VaultWatcher(config.VaultRoot, () => _service.ReloadAll(), ignore: store.IsOwnWrite);

        try
        {
            _host = KanbanApiHost.Start(_service, config.Port, config.Token);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"ローカル API を起動できませんでした（ポート {config.Port}）。\n{ex.Message}",
                "Kanban", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        // ヘッドレス起動（API のみ。UI を出さずに常駐）
        if (e.Args.Contains("--api-only"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            return;
        }

        // 文字サイズ（表示倍率）は通常起動のときだけ反映（開発用モードのスクショ・検証は 100% で行う）
        UiZoom.LoadSaved();
        var vm = new MainViewModel(_service);
        _window = new MainWindow { DataContext = vm };
        MainWindow = _window;

        SetupTray();

        _window.Closing += Window_Closing;
        _window.StateChanged += Window_StateChanged;
        _window.Show();

        ListenForShowRequests();
    }

    private const string ShowEventName = "Kanban.Desktop.ShowWindow";
    private static EventWaitHandle? _showEvent;
    private static RegisteredWaitHandle? _showWait;

    /// <summary>2 回目の起動から「ウィンドウを出して」の合図を受け取る。</summary>
    private void ListenForShowRequests()
    {
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(RestoreWindow), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>常駐中のインスタンスに合図を送る。旧バージョンなど受け手がいなければ false。</summary>
    private static bool SignalRunningInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowEventName);
            ev.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    private void SetupTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => RestoreWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => ExitApp());

        var icon = TryGetAppIcon();
        _tray = new WinForms.NotifyIcon
        {
            Icon = icon,
            Text = "Kanban",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => RestoreWindow();
    }

    private static Drawing.Icon TryGetAppIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var ico = Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico is not null) return ico;
            }
        }
        catch { /* fall through */ }
        return Drawing.SystemIcons.Application;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;
        // × で閉じても常駐させ、タスクトレイに残す
        e.Cancel = true;
        _window?.Hide();
        _tray?.ShowBalloonTip(2000, "Kanban", "タスクトレイに常駐しました。右クリックで終了できます。",
            WinForms.ToolTipIcon.Info);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_window is not null && _window.WindowState == WindowState.Minimized)
            _window.Hide();
    }

    private void RestoreWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void ExitApp()
    {
        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        if (_window is not null)
        {
            _window.Closing -= Window_Closing;
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _watcher?.Dispose();
        _host?.Dispose();
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // ---- screenshot mode (開発用) ----

    private async Task RunScreenshotMode(string dir)
    {
        Directory.CreateDirectory(dir);
        var vm = new MainViewModel(_service!);
        var window = new MainWindow
        {
            DataContext = vm,
            Left = -30000,
            Top = -30000,
            Width = 1560,
            Height = 920,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        window.Show();
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-boards.png"));

        // 複数選択（Ctrl+クリック相当）と、サイドバーの「仕事」グループを閉じた状態
        foreach (var c in vm.Boards.SelectMany(b => b.Items).Take(3).ToList()) vm.ToggleCardSelection(c);
        vm.WorkGroupOpen = false;
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-selection.png"));
        vm.ClearSelection();
        vm.WorkGroupOpen = true;
        await Settle(window);

        // 追加欄に長いタイトルを入れても、カラムが横に伸びずに折り返すこと
        if (vm.Boards.FirstOrDefault() is { } addBoard)
        {
            addBoard.IsAdding = true;
            addBoard.NewItemText = string.Concat(Enumerable.Repeat("とても長いタイトルの折り返し確認 ", 8));
            await Settle(window);
            Shot(window, Path.Combine(dir, "shot-adding.png"));
            addBoard.NewItemText = "";
            addBoard.IsAdding = false;
            await Settle(window);
        }

        // コメント表示も写るよう、コメントのあるカードを優先して開く
        var first = vm.Boards.SelectMany(b => b.Items).FirstOrDefault(c => c.HasComments)
                    ?? vm.Boards.SelectMany(b => b.Items).FirstOrDefault();
        if (first is not null)
        {
            vm.OpenItem(first);
            await Settle(window);
            Shot(window, Path.Combine(dir, "shot-detail.png"));
            // 下側（コメント入力欄・畳んだアクティビティ）
            window.DetailScroll.ScrollToEnd();
            await Settle(window);
            Shot(window, Path.Combine(dir, "shot-detail-bottom.png"));
            // 詳細の右に追加フォームを開いた状態（タグ候補も出す。追加はしない）
            window.DetailScroll.ScrollToTop();
            vm.ToggleQuickAdd();
            if (vm.QuickAdd is { } quick)
            {
                quick.Title = "詳細を見ながら追加するアイテム";
                quick.NewTag = "p-";
            }
            await Settle(window);
            Shot(window, Path.Combine(dir, "shot-detail-quickadd.png"));
            vm.ToggleQuickAdd();
            // 幅を広げた詳細（左右の端のドラッグ相当）を文字サイズ 125% で
            window.ApplyDetailWidth(960);
            UiZoom.Set(1.25, save: false);
            await Settle(window);
            Shot(window, Path.Combine(dir, "shot-detail-wide-zoom.png"));
            UiZoom.Set(1.0, save: false);
            window.ApplyDetailWidth();
            vm.CloseDetail();
        }

        // 設定モーダル（文字サイズ・詳細の幅）
        vm.IsSettingsOpen = true;
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-settings.png"));
        vm.IsSettingsOpen = false;

        // コメント用の簡易 Markdown プレビュー（表を含む見本）
        var mdPanel = new RichTextBox
        {
            Margin = new Thickness(16), IsReadOnly = true, BorderThickness = new Thickness(0), FontSize = 12.5,
        };
        MarkdownMini.SetSource(mdPanel, string.Join("\n",
            "## #1141 調査結果", "", "### 結論",
            "- **保管番地1 = `ws_item_list.i_shelf_num`**", "- [x] 調査済み", "",
            "| output列 | 移行元 | 変換式 | 分布 |", "|---|---|---|---|",
            "| display_restock_flg | i_reserve9 | `TRIM(COALESCE(i_reserve9, ''))` | '1':147 / '0':42,374 |",
            "| sold_out_flg | i_flg_sold | **=1 THEN 1 ELSE 0** | 0:9,270 / 1:54,9xx |",
            "| deleted_at | i_deleted_at | 単純コピー | NULL:64,148 / 非NULL:63 |", "",
            "表のあとの文章。"));
        var mdWindow = new Window
        {
            Content = mdPanel, Width = 640, Height = 420, ShowInTaskbar = false, ShowActivated = false,
            Left = -10000, Top = -10000, Background = System.Windows.Media.Brushes.White,
        };
        mdWindow.Show();
        await Settle(mdWindow);
        Shot(mdWindow, Path.Combine(dir, "shot-markdown.png"));
        mdWindow.Close();

        vm.SelectToday();
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-today.png"));
        vm.SelectToday();

        vm.SidebarCollapsed = true;
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-collapsed.png"));
        vm.SidebarCollapsed = false;

        vm.SidebarQuery = "kan";
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-sidebar-search.png"));
        vm.SidebarQuery = "";

        vm.OpenBoardSettings();
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-board-settings.png"));
        vm.BoardSettings = null;

        // ドラッグ中の見た目（ゴースト + 元の場所の灰色枠 + 落とし先スロット）を再現
        var dragBoard = vm.Boards.FirstOrDefault(b => b.Items.Count >= 4);
        if (dragBoard is not null)
        {
            var dragged = dragBoard.Items[1];
            var cardEl = FindVisual<System.Windows.Controls.Border>(window,
                b => b.Name == "Card" && ReferenceEquals(b.DataContext, dragged));
            if (cardEl is not null)
            {
                var ghost = new DragGhostAdorner(window.RootGrid, cardEl, new Point(60, 20));
                System.Windows.Documents.AdornerLayer.GetAdornerLayer(window.RootGrid)?.Add(ghost);
                var p = cardEl.TranslatePoint(new Point(160, 40), window.RootGrid);
                ghost.MoveTo(p);
                dragged.Dragging = true;
                dragBoard.Items[3].SlotHeight = cardEl.ActualHeight;
                dragBoard.Items[3].DropIndicator = "top";
                await Settle(window);
                Shot(window, Path.Combine(dir, "shot-drag.png"));
                System.Windows.Documents.AdornerLayer.GetAdornerLayer(window.RootGrid)?.Remove(ghost);
                dragged.Dragging = false;
                dragBoard.Items[3].DropIndicator = null;
            }
        }

        vm.ShowTags();
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-tags.png"));

        vm.ShowActivity();
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-activity.png"));

        vm.SearchText = "kanban";
        await Settle(window);
        Shot(window, Path.Combine(dir, "shot-search.png"));

        window.Close();
        Shutdown();
    }

    private sealed class ItemDetailProbe : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        private string _title = "";
        public string Title { get => _title; set => SetProperty(ref _title, value); }
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in FindChildren<T>(c)) yield return d;
        }
    }

    private async Task RunClipboardTest(string outDir)
    {
        Directory.CreateDirectory(outDir);
        const ushort Ctrl = 0x11, A = 0x41, C = 0x43, V = 0x56;
        var saved = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        var sp = new StackPanel { Margin = new Thickness(16) };
        var plain = new System.Windows.Controls.TextBox { Text = "plain-source", Margin = new Thickness(0, 6, 0, 6) };
        var emoji = new Emoji.Wpf.TextBox
        {
            Text = "🔥 emoji-source", Margin = new Thickness(0, 6, 0, 6),
            Style = (Style)FindResource(typeof(System.Windows.Controls.TextBox)),
        };
        var plainTarget = new System.Windows.Controls.TextBox { Text = "x", Margin = new Thickness(0, 6, 0, 6) };
        var emojiTarget = new Emoji.Wpf.TextBox
        {
            Text = "x", Margin = new Thickness(0, 6, 0, 6),
            Style = (Style)FindResource(typeof(System.Windows.Controls.TextBox)),
        };
        foreach (var c in new UIElement[] { plain, emoji, plainTarget, emojiTarget }) sp.Children.Add(c);
        var w = new Window { Content = sp, Width = 420, Height = 260, Title = "clip test", Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        w.Show();
        await Task.Delay(1200);
        var log = new System.Text.StringBuilder();
        w.PreviewKeyDown += (_, ke) => log.AppendLine($"  key {ke.Key} ime={ke.ImeProcessedKey} sys={ke.SystemKey} mod={System.Windows.Input.Keyboard.Modifiers} focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        w.Activate();
        DevInput.SetForegroundWindow(hwnd);
        await Task.Delay(300);
        if (DevInput.GetForegroundWindow() != hwnd)
        {
            log.AppendLine("ABORT: not foreground");
        }
        else
        {
            var cursor = DevInput.CursorPos();
            async Task Copy(System.Windows.Controls.TextBox src, System.Windows.Controls.TextBox dst, string label)
            {
                Clipboard.SetText("before");
                DevInput.SetForegroundWindow(hwnd);
                System.Windows.Input.Keyboard.Focus(src);
                await Task.Delay(200);
                log.AppendLine($"  fg={(DevInput.GetForegroundWindow() == hwnd)} kbfocus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
                DevInput.Chord(Ctrl, A); await Task.Delay(150);
                DevInput.Chord(Ctrl, C); await Task.Delay(300);
                var clip = Clipboard.ContainsText() ? Clipboard.GetText() : "(none)";
                log.AppendLine($"{label} copy: '{clip}' {(clip == src.Text ? "PASS" : "FAIL")}");
                DevInput.SetForegroundWindow(hwnd);
                System.Windows.Input.Keyboard.Focus(dst);
                await Task.Delay(200);
                DevInput.Chord(Ctrl, A); await Task.Delay(150);
                DevInput.Chord(Ctrl, V); await Task.Delay(300);
                log.AppendLine($"{label} paste: '{dst.Text}' {(dst.Text == clip ? "PASS" : "FAIL")}");
            }
            await Copy(plain, plainTarget, "plain->plain");
            await Copy(emoji, emojiTarget, "emoji->emoji");
            await Copy(plain, emojiTarget, "plain->emoji");
            await Copy(emoji, plainTarget, "emoji->plain");

            // IME オン（ひらがな）のまま Ctrl+V
            foreach (var (box, label) in new[] { ((System.Windows.Controls.TextBox)plainTarget, "ime plain"), (emojiTarget, "ime emoji") })
            {
                Clipboard.SetText("imepaste");
                DevInput.SetForegroundWindow(hwnd);
                System.Windows.Input.Keyboard.Focus(box);
                await Task.Delay(150);
                System.Windows.Input.InputMethod.Current.ImeState = System.Windows.Input.InputMethodState.On;
                System.Windows.Input.InputMethod.Current.ImeConversionMode =
                    System.Windows.Input.ImeConversionModeValues.Native | System.Windows.Input.ImeConversionModeValues.FullShape;
                await Task.Delay(300);
                log.AppendLine($"  {label} imeState={System.Windows.Input.InputMethod.Current.ImeState} mode={System.Windows.Input.InputMethod.Current.ImeConversionMode}");
                DevInput.Chord(Ctrl, A); await Task.Delay(150);
                DevInput.Chord(Ctrl, V); await Task.Delay(300);
                log.AppendLine($"{label} paste: '{box.Text}' {(box.Text == "imepaste" ? "PASS" : "FAIL")}");
                System.Windows.Input.InputMethod.Current.ImeState = System.Windows.Input.InputMethodState.Off;
            }
            DevInput.MoveTo(cursor.X, cursor.Y);
        }
        // 2 つ目の引数に vault のコピーがあれば、本物のメイン画面（検索欄・詳細のタイトル/説明）でも確かめる
        var vaultCopy = Environment.GetCommandLineArgs().SkipWhile(a => a != "--cliptest").Skip(2).FirstOrDefault();
        if (vaultCopy is not null)
        {
            _service = new KanbanService(vaultCopy);
            var vm = new MainViewModel(_service);
            var mw = new MainWindow { DataContext = vm, Width = 1200, Height = 800, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true };
            mw.PreviewKeyDown += (_, ke) => log.AppendLine($"  [main] key {ke.Key} mod={System.Windows.Input.Keyboard.Modifiers} focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name} handled={ke.Handled}");
            mw.Show();
            await Task.Delay(1500);
            var mh = new System.Windows.Interop.WindowInteropHelper(mw).Handle;
            async Task PasteInto(System.Windows.Controls.TextBox box, string label)
            {
                Clipboard.SetText("pasted");
                DevInput.SetForegroundWindow(mh);
                System.Windows.Input.Keyboard.Focus(box);
                await Task.Delay(250);
                log.AppendLine($"  fg={(DevInput.GetForegroundWindow() == mh)} focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
                DevInput.Chord(Ctrl, A); await Task.Delay(150);
                DevInput.Chord(Ctrl, V); await Task.Delay(300);
                log.AppendLine($"{label} paste: '{box.Text}' {(box.Text == "pasted" ? "PASS" : "FAIL")}");
                DevInput.Chord(Ctrl, A); await Task.Delay(150);
                Clipboard.SetText("before");
                DevInput.Chord(Ctrl, C); await Task.Delay(300);
                var clip = Clipboard.ContainsText() ? Clipboard.GetText() : "(none)";
                log.AppendLine($"{label} copy: '{clip}' {(clip == box.Text ? "PASS" : "FAIL")}");
            }
            await PasteInto(mw.SearchBox, "main search");
            vm.SearchText = "";
            var card = vm.Boards.SelectMany(b => b.Items).First();
            vm.OpenItem(card);
            await Task.Delay(800);
            await PasteInto(mw.TitleBox, "detail title");
            var desc = FindChildren<System.Windows.Controls.TextBox>(mw.DetailCard).First(t => t.AcceptsReturn);
            await PasteInto(desc, "detail desc");

            // 画像（スクショ）の貼り付けはパスになる。直近のスクショと被らない半端なサイズで、保存した PNG は後で消す
            var pixels = new byte[37 * 23 * 4];
            Array.Fill(pixels, (byte)0x80);
            Clipboard.SetImage(System.Windows.Media.Imaging.BitmapSource.Create(37, 23, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, 37 * 4));
            DevInput.SetForegroundWindow(mh);
            System.Windows.Input.Keyboard.Focus(desc);
            await Task.Delay(250);
            DevInput.Chord(Ctrl, A); await Task.Delay(150);
            DevInput.Chord(Ctrl, V); await Task.Delay(500);
            var shot = desc.Text;
            var shotOk = shot.EndsWith(".png") && File.Exists(shot);
            log.AppendLine($"detail desc image paste: '{shot}' {(shotOk ? "PASS" : "FAIL")}");
            if (shotOk && Path.GetFileName(shot).StartsWith("kanban-paste")) File.Delete(shot);

            // ファイル（エクスプローラーでコピー）の貼り付けもパスになる
            var files = new System.Collections.Specialized.StringCollection { vaultCopy };
            Clipboard.SetFileDropList(files);
            DevInput.Chord(Ctrl, A); await Task.Delay(150);
            DevInput.Chord(Ctrl, V); await Task.Delay(300);
            log.AppendLine($"detail desc file paste: '{desc.Text}' {(desc.Text == vaultCopy ? "PASS" : "FAIL")}");

            // IME オンのままでも同じ
            Clipboard.SetFileDropList(files);
            System.Windows.Input.InputMethod.Current.ImeState = System.Windows.Input.InputMethodState.On;
            await Task.Delay(300);
            DevInput.Chord(Ctrl, A); await Task.Delay(150);
            DevInput.Chord(Ctrl, V); await Task.Delay(300);
            System.Windows.Input.InputMethod.Current.ImeState = System.Windows.Input.InputMethodState.Off;
            log.AppendLine($"detail desc ime file paste: '{desc.Text}' {(desc.Text == vaultCopy ? "PASS" : "FAIL")}");
            vm.CloseDetail();
            await Task.Delay(300);

            // コメント: 入力欄の貼り付け/コピーと、本文（読み取り専用 RichTextBox）の選択コピー
            var withComments = vm.Boards.SelectMany(b => b.Items).First(c => c.HasComments);
            vm.OpenItem(withComments);
            await Task.Delay(1000);
            var commentBox = FindChildren<System.Windows.Controls.TextBox>(mw.DetailCard)
                .First(t => (t.Tag as string)?.StartsWith("コメント") == true);
            await PasteInto(commentBox, "comment input");
            var body = FindChildren<System.Windows.Controls.RichTextBox>(mw.DetailCard).First();
            DevInput.SetForegroundWindow(mh);
            body.BringIntoView();
            await Task.Delay(300);
            var bpt = body.PointToScreen(new Point(10, 8));
            DevInput.Click((int)bpt.X, (int)bpt.Y);
            await Task.Delay(250);
            log.AppendLine($"  body focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
            DevInput.Chord(Ctrl, A); await Task.Delay(150);
            log.AppendLine($"  body selection='{body.Selection.Text.Trim()[..Math.Min(40, body.Selection.Text.Trim().Length)]}' focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
            Clipboard.SetText("before");
            log.AppendLine($"  clip before='{Clipboard.GetText()}'");
            DevInput.Chord(Ctrl, C); await Task.Delay(300);
            var bodyClip = Clipboard.GetText();
            log.AppendLine($"comment body copy: '{bodyClip[..Math.Min(40, bodyClip.Length)]}' {(bodyClip != "before" && bodyClip.Length > 0 ? "PASS" : "FAIL")}");
            // 実際の操作に近い形: 本文をドラッグで選択 → Ctrl+C
            var rtbs = FindChildren<System.Windows.Controls.RichTextBox>(mw.DetailCard).ToList();
            log.AppendLine($"  richtextboxes={rtbs.Count}");
            var cbody = rtbs.Last();
            cbody.BringIntoView();
            await Task.Delay(400);
            var d1 = cbody.PointToScreen(new Point(2, 8));
            var d2 = cbody.PointToScreen(new Point(Math.Min(120, cbody.ActualWidth - 4), 8));
            await DevInput.Drag((int)d1.X, (int)d1.Y, (int)d2.X, (int)d2.Y);
            await Task.Delay(250);
            log.AppendLine($"  drag selection='{cbody.Selection.Text}' focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name} fg={(DevInput.GetForegroundWindow() == mh)}");
            Clipboard.SetText("before");
            DevInput.Chord(Ctrl, C); await Task.Delay(300);
            var dragClip = Clipboard.GetText();
            log.AppendLine($"comment drag copy: '{dragClip}' {(dragClip == cbody.Selection.Text && dragClip.Length > 0 ? "PASS" : "FAIL")}");

            // コメント入力欄をクリックしてから Ctrl+V
            Clipboard.SetText("pasted2");
            commentBox.Text = "";
            var ip = commentBox.PointToScreen(new Point(commentBox.ActualWidth / 2, commentBox.ActualHeight / 2));
            DevInput.Click((int)ip.X, (int)ip.Y);
            await Task.Delay(250);
            log.AppendLine($"  click focus={System.Windows.Input.Keyboard.FocusedElement?.GetType().Name}");
            DevInput.Chord(Ctrl, V); await Task.Delay(300);
            log.AppendLine($"comment input click+paste: '{commentBox.Text}' {(commentBox.Text == "pasted2" ? "PASS" : "FAIL")}");
            vm.CloseDetail();
            await Task.Delay(500);

            // Shift+ホイールで横スクロール
            var cur = DevInput.CursorPos();
            DevInput.SetForegroundWindow(mh);
            // カラム内のカードの上で回す（Inbox 以外の縦に並ぶカラム）。縦にはどこも動かないこと
            var viewport = new Rect(mw.BoardsScroll.PointToScreen(new Point(0, 0)),
                mw.BoardsScroll.PointToScreen(new Point(mw.BoardsScroll.ActualWidth - 40, mw.BoardsScroll.ActualHeight - 40)));
            var cardEl = FindChildren<System.Windows.Controls.Border>(mw.BoardsScroll)
                .Where(b => b.Name == "Card" && b.IsVisible)
                .First(b => viewport.Contains(b.PointToScreen(new Point(b.ActualWidth / 2, b.ActualHeight / 2))));
            var target = (ItemCardVm)cardEl.DataContext;
            var svs = FindChildren<System.Windows.Controls.ScrollViewer>(mw).ToList();
            var vBefore = svs.Select(v => v.VerticalOffset).ToList();
            log.AppendLine($"  scrollviewers={svs.Count} card #{target.Number}");
            mw.AddHandler(UIElement.PreviewMouseWheelEvent, new System.Windows.Input.MouseWheelEventHandler((_, we) =>
                log.AppendLine($"  preview wheel src={we.OriginalSource.GetType().Name} handled={we.Handled} mod={System.Windows.Input.Keyboard.Modifiers}")), true);
            mw.AddHandler(UIElement.MouseWheelEvent, new System.Windows.Input.MouseWheelEventHandler((_, we) =>
                log.AppendLine($"  wheel bubbled to window handled={we.Handled}")), true);
            var bp = cardEl.PointToScreen(new Point(cardEl.ActualWidth / 2, cardEl.ActualHeight / 2));
            log.AppendLine($"  point={bp} window under={(DevInput.TopWindowAt((int)bp.X, (int)bp.Y) == mh ? "main" : "OTHER")} win=({mw.Left},{mw.Top},{mw.ActualWidth}x{mw.ActualHeight})");
            var x0 = mw.BoardsScroll.HorizontalOffset;
            var y0 = mw.BoardsScroll.VerticalOffset;
            DevInput.Key(0x10, true);
            DevInput.Wheel((int)bp.X, (int)bp.Y, -120);
            await Task.Delay(100);
            DevInput.Wheel((int)bp.X, (int)bp.Y, -120);
            DevInput.Key(0x10, false);
            await Task.Delay(700);
            log.AppendLine($"shift+wheel: x {x0:0}->{mw.BoardsScroll.HorizontalOffset:0} y {y0:0}->{mw.BoardsScroll.VerticalOffset:0} (scrollable {mw.BoardsScroll.ScrollableWidth:0}) vertical moved={svs.Where((v, k) => v.VerticalOffset != vBefore[k]).Count()} {(mw.BoardsScroll.HorizontalOffset > x0 && svs.Select(v => v.VerticalOffset).SequenceEqual(vBefore) ? "PASS" : "FAIL")}");
            DevInput.Key(0x10, true);
            DevInput.Wheel((int)bp.X, (int)bp.Y, 240);
            DevInput.Key(0x10, false);
            await Task.Delay(700);
            log.AppendLine($"shift+wheel back: x={mw.BoardsScroll.HorizontalOffset:0} {(mw.BoardsScroll.HorizontalOffset < 1 ? "PASS" : "FAIL")}");

            // Ctrl+クリックで 2 枚選択 → Ctrl+C で番号をまとめてコピー
            var cards = FindChildren<System.Windows.Controls.Border>(mw.BoardsScroll)
                .Where(b => b.Name == "Card" && b.DataContext is ItemCardVm && b.IsVisible).Take(2).ToList();
            DevInput.Key(0x11, true);
            foreach (var c in cards)
            {
                var cp = c.PointToScreen(new Point(c.ActualWidth / 2, c.ActualHeight / 2));
                DevInput.Click((int)cp.X, (int)cp.Y);
                await Task.Delay(250);
            }
            DevInput.Key(0x11, false);
            await Task.Delay(200);
            var nums = cards.Select(c => ((ItemCardVm)c.DataContext).Number).Order().ToList();
            log.AppendLine($"ctrl+click: selected={vm.SelectedCount} detailOpen={vm.Detail is not null} {(vm.SelectedCount == 2 && vm.Detail is null ? "PASS" : "FAIL")}");
            System.Windows.Input.Keyboard.Focus(mw.BoardsScroll);
            Clipboard.SetText("before");
            DevInput.Chord(Ctrl, C); await Task.Delay(300);
            var expect = "kanban " + string.Join(", ", nums);
            var got = Clipboard.GetText();
            log.AppendLine($"ctrl+c numbers: '{got}' {(got == expect ? "PASS" : "FAIL")}");
            DevInput.Chord(0x1B); await Task.Delay(200);
            log.AppendLine($"esc clears: {(vm.HasSelection ? "FAIL" : "PASS")}");
            DevInput.MoveTo(cur.X, cur.Y);
            mw.Close();
        }
        if (saved is not null) Clipboard.SetText(saved);
        File.WriteAllText(Path.Combine(outDir, "clip-test.txt"), log.ToString());
        w.Close();
        Shutdown();
    }

    /// <summary>カラー絵文字の描画方法を比べる（素の TextBlock / 添付プロパティ / Emoji.Wpf.TextBlock / TextBox）。</summary>
    private async Task RunEmojiTest(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var sp = new StackPanel { Margin = new Thickness(16) };
        System.Windows.Controls.TextBlock RunsBlock()
        {
            var tb = new System.Windows.Controls.TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
            tb.Inlines.Add(new System.Windows.Documents.Run("#1058 ") { Foreground = System.Windows.Media.Brushes.Gray, FontFamily = new System.Windows.Media.FontFamily("Consolas") });
            tb.Inlines.Add(new System.Windows.Documents.Run("🔥 燃えるごみ 🍅 ポモドーロ 👍🏽 🇯🇵 ❤️"));
            return tb;
        }
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "1) 素の TextBlock" });
        sp.Children.Add(RunsBlock());
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "2) Behaviors.EmojiRendering" });
        var b = RunsBlock();
        Emoji.Wpf.Behaviors.SetEmojiRendering(b, true);
        sp.Children.Add(b);
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "3) Emoji.Wpf.TextBlock" });
        sp.Children.Add(new Emoji.Wpf.TextBlock { Text = "🔥 燃えるごみ 🍅 ポモドーロ 👍🏽 🇯🇵 ❤️", FontSize = 15, Margin = new Thickness(0, 6, 0, 6) });
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "4) Emoji.Wpf.TextBox + アプリの TextBox スタイル" });
        sp.Children.Add(new Emoji.Wpf.TextBox
        {
            Text = "🔥 燃えるごみ 🍅 編集できる", FontSize = 15, Margin = new Thickness(0, 6, 0, 6),
            Style = (Style)FindResource(typeof(System.Windows.Controls.TextBox)), Padding = new Thickness(12, 6, 12, 6),
        });
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "5) EmojiText（#番号 Run + 絵文字入り Span）" });
        var mixed = new System.Windows.Controls.TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        mixed.Inlines.Add(new System.Windows.Documents.Run("#1058 ") { Foreground = System.Windows.Media.Brushes.Gray, FontFamily = new System.Windows.Media.FontFamily("Consolas") });
        var span = new System.Windows.Documents.Span();
        EmojiText.SetText(span, "🔥 燃えるごみ 🍅 ポモドーロ ❤️ と長めのタイトルで折り返しも確認する テキスト テキスト");
        mixed.Inlines.Add(span);
        sp.Children.Add(mixed);
        // 詳細のタイトル欄と同じ使い方: バインド + LostFocus が動くか（実際にクリックして打鍵する）
        var holder = new ItemDetailProbe { Title = "🔥 タイトル" };
        var titleBox = new Emoji.Wpf.TextBox { FontSize = 15, Margin = new Thickness(0, 6, 0, 6), DataContext = holder };
        titleBox.SetBinding(System.Windows.Controls.TextBox.TextProperty,
            new System.Windows.Data.Binding(nameof(ItemDetailProbe.Title)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged, Mode = System.Windows.Data.BindingMode.TwoWay });
        var lostFocus = 0;
        titleBox.LostFocus += (_, _) => lostFocus++;
        sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "6) タイトル欄（バインド + 打鍵）" });
        sp.Children.Add(titleBox);
        var other = new System.Windows.Controls.TextBox { Text = "別の欄", Margin = new Thickness(0, 6, 0, 6) };
        sp.Children.Add(other);
        var w = new Window { Content = sp, Left = 40, Top = 40, Width = 560, Height = 520, Title = "emoji test", Topmost = true };
        w.Show();
        await Task.Delay(1500);
        var log = new System.Text.StringBuilder();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        w.Activate();
        DevInput.SetForegroundWindow(hwnd);
        await Task.Delay(300);
        if (DevInput.GetForegroundWindow() == hwnd)
        {
            var saved = DevInput.CursorPos();
            var p = titleBox.PointToScreen(new Point(titleBox.ActualWidth - 20, titleBox.ActualHeight / 2));
            DevInput.Click((int)p.X, (int)p.Y);
            await Task.Delay(300);
            System.Windows.Input.Keyboard.Focus(titleBox);
            titleBox.CaretIndex = titleBox.Text.Length;
            if (DevInput.GetForegroundWindow() == hwnd) DevInput.Type("abc");
            await Task.Delay(400);
            other.Focus();
            await Task.Delay(200);
            DevInput.MoveTo(saved.X, saved.Y);
            log.AppendLine($"title binding: '{holder.Title}' {(holder.Title.EndsWith("abc") ? "PASS" : "FAIL")}");
            log.AppendLine($"title LostFocus fired: {lostFocus} {(lostFocus > 0 ? "PASS" : "FAIL")}");
        }
        else log.AppendLine("ABORT: not foreground");
        File.WriteAllText(Path.Combine(outDir, "emoji-test.txt"), log.ToString());
        Shot(w, Path.Combine(outDir, "emoji.png"));
        w.Close();
        Shutdown();
    }

    private async Task RunMarkdownTest(string mdPath, string outPng)
    {
        var md = File.ReadAllText(mdPath);
        // コメントファイルの frontmatter は落とす
        if (md.StartsWith("---"))
        {
            var end = md.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0) md = md[(end + 4)..].TrimStart('\r', '\n');
        }
        var rtb = new RichTextBox
        {
            IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
            Padding = new Thickness(0), FontSize = 12.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        MarkdownMini.SetSource(rtb, md);
        // 詳細モーダル（最大幅 672）内のコメント欄と同じくらいの幅・余白
        var card = new Border
        {
            Child = rtb, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(24),
            BorderThickness = new Thickness(1), BorderBrush = System.Windows.Media.Brushes.LightGray,
        };
        var scroll = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var w = new Window
        {
            Content = scroll, Width = 660, Height = 1400, ShowInTaskbar = false, ShowActivated = false,
            Left = -10000, Top = -10000, Background = System.Windows.Media.Brushes.White,
        };
        w.Show();
        await Settle(w);
        Shot(w, outPng);
        w.Close();
        Shutdown();
    }

    private static void RunSelfTest(string root)
    {
        var log = new System.Text.StringBuilder();
        void Check(string name, bool ok) => log.AppendLine($"{(ok ? "PASS" : "FAIL")} {name}");
        try
        {
            var svc = new KanbanService(root);
            var vm = new MainViewModel(svc);
            var admin = vm.TagsAdmin;
            vm.ShowTags();

            admin.NewName = "zz-selftest";
            admin.NewEmoji = "🧪";
            admin.CreateCommand.Execute(null);
            var t = svc.ListTags(true).Select(x => x.Tag).FirstOrDefault(x => x.Name == "zz-selftest");
            Check("create tag", t is not null && t.Emoji == "🧪");
            Check("auto color on create", t?.Color == TagPalette.Pick("zz-selftest"));
            var row = admin.OtherTags.FirstOrDefault(r => r.Model.Name == "zz-selftest");
            Check("row listed in その他", row is not null);

            row!.Name = "zz-renamed"; row.CommitName();
            Check("rename", svc.ListTags(true).Any(x => x.Tag.Name == "zz-renamed"));
            row = admin.OtherTags.First(r => r.Id == t!.Id);
            row.SetColorCommand.Execute("#123456");
            Check("set color", svc.GetTag(t!.Id).Color == "#123456");
            row.ToggleFavoriteCommand.Execute(null);
            Check("favorite", svc.GetTag(t.Id).Favorite);
            row.Path = "C:/tmp/x"; row.CommitPath();
            row.Description = "desc"; row.CommitDescription();
            Check("path/description", svc.GetTag(t.Id).Path == "C:/tmp/x" && svc.GetTag(t.Id).Description == "desc");
            var parent = admin.ParentOptions.FirstOrDefault(p => p.Id is not null);
            if (parent is not null)
            {
                row.SelectedParent = parent;
                Check("parent", svc.GetTag(t.Id).ParentTagId == parent.Id);
            }
            row.ToggleArchiveCommand.Execute(null);
            Check("archive moves to アーカイブ済み", svc.GetTag(t.Id).Archived && admin.ArchivedTags.Any(r => r.Id == t.Id));
            admin.Filter = "zz-ren";
            Check("filter", admin.FilteredCount == 1);
            admin.Filter = "";

            // 絞り込み中の追加: 選択中のタグが付き、そのまま表示される
            vm.SwitchToBoards();
            var cat = vm.ProjectTags.FirstOrDefault() ?? vm.WorkTags.FirstOrDefault();
            var inbox = vm.Boards.FirstOrDefault();
            if (cat is not null && inbox is not null)
            {
                vm.SelectCategoryTag(cat, false);
                vm.AddItem(inbox, "selftest item");
                var added = svc.Search("selftest item").Results.Select(r => r.Item).FirstOrDefault();
                Check("add under tag filter attaches tag", added is not null && added.Tags.Any(x => x.Name == cat.Label));
                Check("added item visible under filter",
                    vm.Boards.First(b => b.Model.Id == inbox.Model.Id).Items.Any(c => c.Title == "selftest item"));
                Check("added item is at the top",
                    vm.Boards.First(b => b.Model.Id == inbox.Model.Id).Items.FirstOrDefault()?.Title == "selftest item");
            }
            // Done のページング × 絞り込み: 絞り込み後の一覧を 30 件ずつ出す
            var doneBoard = vm.Boards.FirstOrDefault(b => b.IsDone);
            if (doneBoard is not null)
            {
                var allDone = svc.ListItemsForBoard(doneBoard.Model.Id, includes: new[] { "tags" }).Items;
                var catTag = allDone.SelectMany(s => s.Tags).Where(t => TagRules.IsCategory(t.Name))
                    .GroupBy(t => t.Name).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
                var catEntry = vm.ProjectTags.Concat(vm.WorkTags).FirstOrDefault(t => t.Label == catTag);
                if (catEntry is not null)
                {
                    vm.SelectCategoryTag(catEntry, false);
                    var done = vm.Boards.First(b => b.IsDone);
                    var expected = allDone.Count(s => s.Tags.Any(t => t.Name == catTag));
                    Check($"done filtered total ({catTag}: {expected})", done.Total == expected);
                    Check("done first page is filtered", done.Items.Count == Math.Min(30, expected)
                        && done.Items.All(c => c.Tags.Any(t => t.Name == catTag)));
                    if (done.HasMore)
                    {
                        vm.LoadMore(done);
                        Check("done load more adds filtered items", done.Items.Count == Math.Min(60, expected)
                            && done.Items.All(c => c.Tags.Any(t => t.Name == catTag)));
                    }
                    vm.SelectCategoryTag(null, false);
                }
                var done2 = vm.Boards.First(b => b.IsDone);
                var localTag = done2.FilterTags.FirstOrDefault(ft => allDone.Count(s => s.Tags.Any(t => t.Name == ft.Name)) > 0);
                if (localTag is not null)
                {
                    var expected = allDone.Count(s => s.Tags.Any(t => t.Name == localTag.Name));
                    localTag.Selected = true;
                    Check($"done local filter refetches ({localTag.Name}: {expected})",
                        done2.Total == expected && done2.Items.Count == Math.Min(30, expected)
                        && done2.Items.All(c => c.Tags.Any(t => t.Name == localTag.Name)));
                    localTag.Selected = false;
                    Check("done local filter cleared", done2.Total == allDone.Count && done2.Items.Count == Math.Min(30, allDone.Count));
                }
            }

            vm.SelectToday();
            vm.AddItem(vm.Boards.First(), "selftest today");
            var today = svc.Search("selftest today").Results.Select(r => r.Item).FirstOrDefault();
            Check("add under today filter sets due today", today?.Item.DueDate == DateTime.Today.ToString("yyyy-MM-dd"));
            vm.SelectToday();

            // 詳細を開いたまま右の追加フォームから追加できる
            var opened = vm.Boards.SelectMany(b => b.Items).First();
            vm.OpenItem(opened);
            vm.ToggleQuickAdd();
            var quick = vm.QuickAdd;
            var inboxBoard = svc.ListBoards(vm.SelectedProject!.Model.Id).FirstOrDefault(b => b.Name == "Inbox");
            Check("quick add defaults to Inbox", inboxBoard is not null && quick?.SelectedBoardOption?.Id == inboxBoard.Id);
            if (quick is not null)
            {
                var tagName = svc.ListTags().Select(x => x.Tag).First().Name;
                quick.Title = "selftest quick add";
                quick.Priority = "high";
                quick.SetDue("tomorrow");
                quick.NewTag = tagName;
                quick.AttachTag();
                quick.NewTag = "zz-no-such-tag";
                quick.AttachTag();
                Check("quick add attaches only existing tags", quick.Tags.Select(t => t.Name).SequenceEqual(new[] { tagName }));
                Check("quick add create", quick.Create());
                var added = svc.Search("selftest quick add").Results.Select(r => r.Item).FirstOrDefault();
                Check("quick add fields", added is not null && added.Item.Priority == "high"
                    && added.Item.DueDate == DateTime.Today.AddDays(1).ToString("yyyy-MM-dd")
                    && added.Tags.Select(t => t.Name).SequenceEqual(new[] { tagName }));
                Check("quick add keeps detail open and clears title",
                    vm.Detail?.ItemId == opened.Item.Id && vm.QuickAdd == quick && quick.Title == "");
                Check("quick add rejects empty title", !quick.Create());
            }
            vm.CloseDetail();
            Check("closing detail closes quick add", vm.QuickAdd is null);

            // サイドバーの件数は Inbox / Assigned / Doing / Review の合計
            vm.Reload();
            var countedNames = new[] { "Inbox", "Assigned", "Doing", "Review" };
            var expectedPending = svc.ListBoards(vm.SelectedProject!.Model.Id)
                .Where(b => countedNames.Contains(b.Name))
                .Sum(b => svc.ListItemsForBoard(b.Id).Total);
            Check($"sidebar count = Inbox+Assigned+Doing+Review ({vm.AllEntry.Count}/{expectedPending})", vm.AllEntry.Count == expectedPending);
        }
        catch (Exception ex)
        {
            log.AppendLine("ERROR " + ex);
        }
        File.WriteAllText(Path.Combine(root, "selftest-result.txt"), log.ToString());
    }

    private async Task RunBenchMode(string outPath, string vaultRoot)
    {
        var log = new System.Text.StringBuilder();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _service = new KanbanService(vaultRoot);
        log.AppendLine($"service load: {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        _service.ReloadAll();
        log.AppendLine($"ReloadAll (watcher path): {sw.ElapsedMilliseconds} ms");

        var vm = new MainViewModel(_service);
        var window = new MainWindow
        {
            DataContext = vm, Left = -30000, Top = -30000, Width = 1560, Height = 920,
            ShowInTaskbar = false, ShowActivated = false,
        };
        sw.Restart();
        window.Show();
        await Rendered(window);
        log.AppendLine($"first show+render: {sw.ElapsedMilliseconds} ms");

        for (var round = 0; round < 3; round++)
        {
            sw.Restart();
            vm.Reload();
            await Rendered(window);
            log.AppendLine($"[{round}] Reload+render: {sw.ElapsedMilliseconds} ms");

            var card = vm.Boards.SelectMany(b => b.Items).FirstOrDefault();
            if (card is null) break;
            sw.Restart();
            vm.OpenItem(card);
            await Rendered(window);
            log.AppendLine($"[{round}] open item+render: {sw.ElapsedMilliseconds} ms");

            sw.Restart();
            vm.CloseDetail();
            await Rendered(window);
            log.AppendLine($"[{round}] close item+render: {sw.ElapsedMilliseconds} ms");

            sw.Restart();
            vm.SelectToday();
            await Rendered(window);
            vm.SelectToday();
            await Rendered(window);
            log.AppendLine($"[{round}] filter today on/off+render: {sw.ElapsedMilliseconds} ms");
        }

        // なめらかスクロール: ホイール 1 ノッチで位置がフレームごとに少しずつ進むか
        {
            var board = vm.Boards.Where(b => !b.IsBoardLayout).OrderByDescending(b => b.Items.Count).First();
            var list = FindVisual<ListBox>(window, l => ReferenceEquals(l.DataContext, board));
            var sv = list is null ? null : FindVisual<ScrollViewer>(list, _ => true);
            var item = list is null ? null : FindVisual<ListBoxItem>(list, _ => true);
            if (sv is not null && item is not null)
            {
                sv.ScrollToVerticalOffset(0);
                await Rendered(window);
                var samples = new List<string>();
                item.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = UIElement.MouseWheelEvent });
                var t0 = System.Diagnostics.Stopwatch.StartNew();
                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(10);
                    samples.Add($"{t0.ElapsedMilliseconds}ms:{sv.VerticalOffset:F0}");
                }
                log.AppendLine($"smooth wheel ({board.Name}): offsets {string.Join(",", samples)}");
            }
        }

        // TextBox のキャレットとプレースホルダーの位置（ずれ検出用）
        {
            var tb = new System.Windows.Controls.TextBox { Width = 260, Padding = new Thickness(12, 8, 12, 8), Tag = "タイトル..." };
            var probe = new Window { Content = tb, Left = -30000, Top = -30000, Width = 400, Height = 200,
                ShowInTaskbar = false, ShowActivated = false };
            probe.Show();
            await Rendered(probe);
            var caret = tb.GetRectFromCharacterIndex(0);
            var ph = tb.Template.FindName("Ph", tb) as FrameworkElement;
            var phPos = ph?.TranslatePoint(new Point(0, 0), tb) ?? new Point(-1, -1);
            log.AppendLine($"textbox probe: caret.X={caret.X:F1} caret.Y={caret.Y:F1} placeholder.X={phPos.X:F1} placeholder.Y={phPos.Y:F1}");
            probe.Close();
        }

        // Done のページング
        var done = vm.Boards.FirstOrDefault(b => b.IsDone);
        if (done is not null)
        {
            log.AppendLine($"done initial: loaded={done.Loaded} items={done.Items.Count} total={done.Total} hasMore={done.HasMore}");
            sw.Restart();
            vm.LoadMore(done);
            await Rendered(window);
            log.AppendLine($"done LoadMore+render: {sw.ElapsedMilliseconds} ms -> loaded={done.Loaded} items={done.Items.Count}");

            // 一番下までスクロールして自動取得されるか
            var list = FindVisual<System.Windows.Controls.ListBox>(window, l => ReferenceEquals(l.DataContext, done));
            var sv = list is null ? null : FindVisual<System.Windows.Controls.ScrollViewer>(list, _ => true);
            if (sv is not null)
            {
                for (var i = 0; i < 2; i++)
                {
                    var before = done.Loaded;
                    var beforeOffset = sv.ScrollableHeight;
                    sv.ScrollToVerticalOffset(sv.ScrollableHeight);
                    await Rendered(window);
                    await Task.Delay(200);
                    await Rendered(window);
                    log.AppendLine($"done scroll to bottom #{i}: loaded {before} -> {done.Loaded}, offset kept={sv.VerticalOffset:F0} (was bottom {beforeOffset:F0})");
                }
            }
            vm.Reload();
            await Rendered(window);
            log.AppendLine($"done after Reload keeps pages: loaded={done.Loaded}");
        }

        File.WriteAllText(outPath, log.ToString());
        window.Close();
        Shutdown();
    }

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> pred) where T : DependencyObject
    {
        var n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < n; i++)
        {
            var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (c is T t && pred(t)) return t;
            var r = FindVisual(c, pred);
            if (r is not null) return r;
        }
        return null;
    }

    /// <summary>レイアウトと描画が一巡するまで待つ（ContextIdle まで流す）。</summary>
    private static async Task Rendered(Window w)
    {
        await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private static async Task Settle(Window w)
    {
        // レイアウト・バインディング反映待ち
        await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        await Task.Delay(300);
        await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
    }

    private static void Shot(Window w, string path)
    {
        var dpi = 96.0;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)w.ActualWidth, (int)w.ActualHeight, dpi, dpi,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(w);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
