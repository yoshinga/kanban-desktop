using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Kanban.Desktop;

/// <summary>
/// クリップボードに文字列が無く、画像（スクショ）かファイルがあるときの貼り付けで、そのファイルのパスを入力欄に入れる。
/// 画像は Snipping Tool が自動保存したスクショ（Pictures\Screenshots の直近のもので、縦横のピクセル数が同じ）があればそのパス、
/// 無ければ同じフォルダーに PNG で保存してそのパス。Ctrl+V・右クリックの貼り付け・IME オン中（ImeShortcuts 経由）のどれでも効く。
/// </summary>
public static class PastePath
{
    /// <summary>自動保存されたスクショとみなす新しさ。</summary>
    private static readonly TimeSpan Recent = TimeSpan.FromMinutes(10);

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(TextBoxBase), CommandManager.PreviewCanExecuteEvent,
            new CanExecuteRoutedEventHandler(OnPreviewCanExecute));
        EventManager.RegisterClassHandler(typeof(TextBoxBase), CommandManager.PreviewExecutedEvent,
            new ExecutedRoutedEventHandler(OnPreviewExecuted));
    }

    private static void OnPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !Applies(sender)) return;
        e.CanExecute = true;
        e.Handled = true;
    }

    private static void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste || !Applies(sender)) return;
        e.Handled = true;
        string? text;
        try { text = ClipboardPaths(); }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            text = null;
        }
        if (string.IsNullOrEmpty(text)) return;
        switch (sender)
        {
            case TextBox box:
                var start = box.SelectionStart;
                box.SelectedText = text;
                box.Select(start + text.Length, 0);
                break;
            case RichTextBox rich:
                rich.Selection.Text = text;
                rich.Selection.Select(rich.Selection.End, rich.Selection.End);
                break;
        }
    }

    /// <summary>編集できる入力欄で、クリップボードが文字列ではなく画像かファイルのときだけ横取りする。</summary>
    private static bool Applies(object sender)
    {
        if (sender is not TextBoxBase { IsReadOnly: false, IsEnabled: true }) return false;
        try { return !Clipboard.ContainsText() && (Clipboard.ContainsImage() || Clipboard.ContainsFileDropList()); }
        catch (COMException) { return false; }
    }

    private static string? ClipboardPaths()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>().ToList();
            return files.Count == 0 ? null : string.Join(Environment.NewLine, files);
        }
        var png = ClipboardPng();
        return png is null ? null : ScreenshotPath(png);
    }

    /// <summary>クリップボードの画像を PNG のバイト列で。Snipping Tool などが置く "PNG" 形式を優先（透過も崩れない）。</summary>
    private static byte[]? ClipboardPng()
    {
        if (Clipboard.GetData("PNG") is MemoryStream ms) return ms.ToArray();
        var image = Clipboard.GetImage();
        if (image is null) return null;
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static string ScreenshotPath(byte[] png)
    {
        var dir = ScreenshotsFolder();
        var size = PngSize(png);
        if (size is not null && Directory.Exists(dir))
        {
            var saved = new DirectoryInfo(dir).EnumerateFiles("*.png")
                .Where(f => DateTime.Now - f.LastWriteTime < Recent)
                .OrderByDescending(f => f.LastWriteTime)
                .FirstOrDefault(f => PngSize(f.FullName) == size);
            if (saved is not null) return saved.FullName;
        }
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"kanban-paste {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"kanban-paste {DateTime.Now:yyyy-MM-dd HHmmss} ({i}).png");
        File.WriteAllBytes(path, png);
        return path;
    }

    /// <summary>PNG の IHDR から幅と高さを読む。PNG でなければ null。</summary>
    private static (int W, int H)? PngSize(ReadOnlySpan<byte> png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[12] != (byte)'I' || png[13] != (byte)'H') return null;
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[16..]),
            System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png[20..]));
    }

    private static (int W, int H)? PngSize(string file)
    {
        try
        {
            using var fs = File.OpenRead(file);
            var head = new byte[24];
            return fs.Read(head, 0, head.Length) == head.Length ? PngSize(head) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Windows のスクリーンショット フォルダー（既知フォルダー。移動されていても追える）。</summary>
    private static string ScreenshotsFolder()
    {
        var id = new Guid("B7BEDE81-DF94-4682-A7D8-57A52620B86F");
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) == 0)
        {
            try { return Marshal.PtrToStringUni(ptr)!; }
            finally { Marshal.FreeCoTaskMem(ptr); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);
}
