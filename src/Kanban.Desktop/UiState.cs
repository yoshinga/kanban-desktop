using System.IO;
using System.Text.Json;

namespace Kanban.Desktop;

/// <summary>
/// UI の小さな状態（選択中プロジェクト・サイドバーとグループの開閉）を
/// %LOCALAPPDATA%\Kanban\ui.json に保存する。
/// </summary>
public sealed class UiState
{
    public Guid? ProjectId { get; set; }
    public bool SidebarCollapsed { get; set; }
    /// <summary>サイドバーの「仕事」グループを閉じているか。</summary>
    public bool WorkGroupClosed { get; set; }
    /// <summary>サイドバーの「プロジェクト」グループを閉じているか。</summary>
    public bool ProjectGroupClosed { get; set; }
    /// <summary>画面全体の表示倍率（文字サイズ）。1.0 = 100%。</summary>
    public double UiScale { get; set; } = 1.0;
    /// <summary>詳細モーダルの幅（ドラッグで変更したもの）。null なら既定幅。</summary>
    public double? DetailWidth { get; set; }

    public const double DefaultDetailWidth = 672;
    /// <summary>詳細モーダルの表示幅。開発用モード（保存しない）では保存値を使わず既定幅。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public double DetailWidthOrDefault => Persist && DetailWidth is { } w ? w : DefaultDetailWidth;

    private static UiState? _current;
    /// <summary>プロセス内で共有するインスタンス（別々に Load して保存し合うと上書きし合うため）。</summary>
    public static UiState Current => _current ??= Load();

    /// <summary>false にすると保存しない（スクリーンショットモード用）。</summary>
    public static bool Persist { get; set; } = true;

    private static string PathName => System.IO.Path.Combine(AppConfig.Dir, "ui.json");

    public static UiState Load()
    {
        try
        {
            if (File.Exists(PathName))
                return JsonSerializer.Deserialize<UiState>(File.ReadAllText(PathName)) ?? new UiState();
        }
        catch { /* 壊れていたら既定値 */ }
        return new UiState();
    }

    public void Save()
    {
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(AppConfig.Dir);
            File.WriteAllText(PathName, JsonSerializer.Serialize(this));
        }
        catch { /* UI 状態の保存失敗は無視 */ }
    }
}
