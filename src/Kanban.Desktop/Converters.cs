using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Kanban.Desktop;

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { /* ignore */ }
        }
        return new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// アイテム id から決定的に淡いパステル背景を選ぶ。
/// 16 色パステルパレット (Tailwind *-50) と FNV-1a + Murmur3 ハッシュを使い、
/// 「あれは緑のタスク」のような視覚記憶を再現する。
/// </summary>
public sealed class PastelBrushConverter : IValueConverter
{
    private static readonly Brush[] Palette = BuildPalette();

    private static Brush[] BuildPalette()
    {
        string[] hexes =
        {
            "#FFF1F2", "#ECFEFF", "#F7FEE7", "#F5F3FF",
            "#FFFBEB", "#EFF6FF", "#ECFDF5", "#FDF4FF",
            "#FFF7ED", "#F0F9FF", "#F0FDF4", "#FAF5FF",
            "#FEFCE8", "#EEF2FF", "#F0FDFA", "#FDF2F8",
        };
        return hexes.Select(h => Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)))).ToArray();
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private static uint HashId(string id)
    {
        unchecked
        {
            uint h = 0x811c9dc5;
            foreach (var ch in id)
            {
                h ^= ch;
                h *= 0x01000193;
            }
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;
            return h;
        }
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var id = value as string ?? "";
        return Palette[HashId(id) % Palette.Length];
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 期限ステータス (overdue / today / soon / future) に応じたブラシ。
/// parameter = "bg" (背景) / "fg" (文字) / "ring" (枠)。
/// </summary>
public sealed class DueBrushConverter : IValueConverter
{
    private static readonly Brush OverdueBg = Frozen("#EF4444");
    private static readonly Brush OverdueFg = Frozen("#FFFFFF");
    private static readonly Brush TodayBg = Frozen("#F97316");
    private static readonly Brush TodayFg = Frozen("#FFFFFF");
    private static readonly Brush SoonBg = Frozen("#FFF7ED");
    private static readonly Brush SoonFg = Frozen("#C2410C");
    private static readonly Brush SoonRing = Frozen("#FDBA74");
    private static readonly Brush FutureBg = Frozen("#F5F5F5");
    private static readonly Brush FutureFg = Frozen("#525252");
    private static readonly Brush FutureRing = Frozen("#E5E5E5");
    private static readonly Brush Transparent = Brushes.Transparent;

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var status = value as string;
        var part = parameter as string ?? "bg";
        return (status, part) switch
        {
            ("overdue", "bg") => OverdueBg,
            ("overdue", "fg") => OverdueFg,
            ("today", "bg") => TodayBg,
            ("today", "fg") => TodayFg,
            ("soon", "bg") => SoonBg,
            ("soon", "fg") => SoonFg,
            ("soon", "ring") => SoonRing,
            ("future", "bg") => FutureBg,
            ("future", "fg") => FutureFg,
            ("future", "ring") => FutureRing,
            _ => part == "fg" ? FutureFg : part == "ring" ? FutureRing : Transparent,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>int &gt; 0 で Visible。parameter="invert" なら 0 のとき Visible。</summary>
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var positive = value is int i && i > 0;
        if (parameter as string == "invert") positive = !positive;
        return positive ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 文字列一致 ↔ bool (RadioButton の IsChecked 用)。
/// ConvertBack は true のとき parameter を返し、false なら DoNothing。
/// </summary>
public sealed class StringMatchConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? (parameter ?? "") : Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非空文字列で Visible（検索クリアボタンなど）。</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
