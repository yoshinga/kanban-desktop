namespace Kanban.Core.Vault;

/// <summary>
/// Watches the vault folder and triggers a debounced reload when files change
/// (external edits, Dropbox sync, agents writing directly).
/// </summary>
public sealed class VaultWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Action _onChanged;
    private readonly Func<string, bool>? _ignore;
    private readonly System.Timers.Timer _debounce;
    private readonly object _gate = new();

    /// <param name="ignore">true を返したパスのイベントは無視する（自分の書き込みなど）。</param>
    public VaultWatcher(string root, Action onChanged, int debounceMs = 400, Func<string, bool>? ignore = null)
    {
        _onChanged = onChanged;
        _ignore = ignore;
        _debounce = new System.Timers.Timer(debounceMs) { AutoReset = false };
        _debounce.Elapsed += (_, _) =>
        {
            lock (_gate) { /* serialize */ }
            try { _onChanged(); } catch { }
        };

        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            Filter = "*.*",
            EnableRaisingEvents = false,
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Deleted += OnFsEvent;
        _watcher.Renamed += OnFsEvent;
        _watcher.Error += (_, _) => Schedule();
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return;
        if (e.FullPath.Contains(".tmp-", StringComparison.OrdinalIgnoreCase)) return;
        // フォルダ自身の更新通知（中のファイルを作る・置き換えるたびに出る）は無視する。
        // 外部の変更はファイル側のイベントで拾えるので、これを拾うと自分の書き込みでも全件リロードになる
        if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath)) return;
        if (_ignore?.Invoke(e.FullPath) == true) return;
        Schedule();
    }

    private void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
