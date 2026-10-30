using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Kanban.Desktop;

/// <summary>
/// 日本語 IME がオンのとき、Ctrl+V などのキーは Key.ImeProcessed として届き（本当のキーは ImeProcessedKey）、
/// WPF 標準の編集ショートカット（KeyGesture）が反応しない。右クリックメニューからの貼り付けは効くのに
/// Ctrl+V だけ効かない、という症状になる。入力欄ではここで本当のキーを見て編集コマンドを実行する。
/// </summary>
public static class ImeShortcuts
{
    /// <summary>IME が処理したキーなら本当のキーを返す。</summary>
    public static Key RealKey(this KeyEventArgs e) => e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler(OnPreviewKeyDown));
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.ImeProcessed || sender is not TextBoxBase box) return;
        var shift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
        if (Keyboard.Modifiers != ModifierKeys.Control && !shift) return;
        RoutedUICommand? command = (e.ImeProcessedKey, shift) switch
        {
            (Key.V, false) => ApplicationCommands.Paste,
            (Key.C, false) => ApplicationCommands.Copy,
            (Key.X, false) => ApplicationCommands.Cut,
            (Key.A, false) => ApplicationCommands.SelectAll,
            (Key.Z, false) => ApplicationCommands.Undo,
            (Key.Y, false) or (Key.Z, true) => ApplicationCommands.Redo,
            _ => null,
        };
        if (command is null) return;
        if (command.CanExecute(null, box)) command.Execute(null, box);
        e.Handled = true;
    }
}
