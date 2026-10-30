using System.Runtime.InteropServices;

namespace Kanban.Desktop;

/// <summary>
/// 開発用（--clicktest）: 実際のマウスクリックとキー入力を OS に送る。
/// WebView2 のフォーカスまわりは合成イベントでは再現できないため、本物の入力で確かめる。
/// </summary>
internal static class DevInput
{
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    /// <summary>画面上のその位置にあるトップレベルウィンドウ。</summary>
    public static IntPtr TopWindowAt(int x, int y) => GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2);

    public static POINT CursorPos() { GetCursorPos(out var p); return p; }
    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        var down = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0002 } } };
        var up = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } } };
        SendInput(2, new[] { down, up }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>左ボタンでドラッグする（押す → 少しずつ動かす → 離す）。</summary>
    public static async Task Drag(int x1, int y1, int x2, int y2)
    {
        SetCursorPos(x1, y1);
        SendInput(1, new[] { new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0002 } } } }, Marshal.SizeOf<INPUT>());
        for (var i = 1; i <= 10; i++)
        {
            await Task.Delay(20);
            SetCursorPos(x1 + (x2 - x1) * i / 10, y1 + (y2 - y1) * i / 10);
        }
        await Task.Delay(50);
        SendInput(1, new[] { new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } } } }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>ホイールを回す（delta=120 で 1 ノッチ上、負で下）。</summary>
    public static void Wheel(int x, int y, int delta)
    {
        SetCursorPos(x, y);
        var wheel = new INPUT { type = 0, u = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0800, mouseData = unchecked((uint)delta) } } };
        SendInput(1, new[] { wheel }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>キーを押したまま / 離す。</summary>
    public static void Key(ushort vk, bool down)
    {
        var k = new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : 0x0002 } } };
        SendInput(1, new[] { k }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>仮想キーの同時押し（例: Ctrl+C = Chord(0x11, 0x43)）。押した順と逆に離す。</summary>
    public static void Chord(params ushort[] vks)
    {
        var list = new List<INPUT>();
        foreach (var vk in vks)
            list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } });
        foreach (var vk in vks.Reverse())
            list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0x0002 } } });
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    /// <summary>Unicode 文字として打鍵する（IME を通さない）。</summary>
    public static void Type(string text)
    {
        var list = new List<INPUT>();
        foreach (var ch in text)
        {
            list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 } } });
            list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = 0x0004 | 0x0002 } } });
        }
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }
}
