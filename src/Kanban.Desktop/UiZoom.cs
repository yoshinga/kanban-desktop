using System.Windows.Media;

namespace Kanban.Desktop;

/// <summary>
/// 画面全体の文字サイズ（表示倍率）。文字サイズは各所に直書きしているので、
/// ウィンドウの中身・ポップアップ・ツールチップ・右クリックメニューに同じ拡大を掛けてまとめて大きくする。
/// XAML からは <c>LayoutTransform="{Binding Source={x:Static local:UiZoom.Transform}}"</c> で参照する
/// （Style の Setter に Freezable を直接置くと凍結されて倍率を変えられないため Binding 経由）。
/// </summary>
public static class UiZoom
{
    public const double Min = 0.8;
    public const double Max = 1.6;
    public const double Step = 0.05;

    /// <summary>全要素で共有する拡大。倍率を変えると参照している要素がすべて描き直される。</summary>
    public static ScaleTransform Transform { get; } = new(1, 1);

    public static double Scale => Transform.ScaleX;

    public static event Action? Changed;

    public static double Clamp(double scale) => Math.Round(Math.Clamp(scale, Min, Max) / Step) * Step;

    /// <summary>倍率を変えて ui.json に保存する。</summary>
    public static void Set(double scale, bool save = true)
    {
        scale = Clamp(scale);
        if (Math.Abs(scale - Scale) > 0.001)
        {
            Transform.ScaleX = scale;
            Transform.ScaleY = scale;
            Changed?.Invoke();
        }
        if (save)
        {
            UiState.Current.UiScale = scale;
            UiState.Current.Save();
        }
    }

    /// <summary>起動時に保存済みの倍率を反映する。</summary>
    public static void LoadSaved() => Set(UiState.Current.UiScale is > 0 and var s ? s : 1.0, save: false);
}
