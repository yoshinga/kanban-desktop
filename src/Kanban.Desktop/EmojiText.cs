using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Emoji.Wpf;

namespace Kanban.Desktop;

/// <summary>
/// カラー絵文字の表示。WPF は標準ではカラー絵文字を描けず白黒になるので、Emoji.Wpf を使って
/// テキスト中の絵文字だけをカラーの EmojiInline に置き換える。
/// Span / TextBlock に <c>local:EmojiText.Text="{Binding ...}"</c> を付けて使う
/// （Span なら「#番号」など他の Run と同じ TextBlock の中に混ぜられる）。
/// </summary>
public static class EmojiText
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(EmojiText), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var text = e.NewValue as string ?? "";
        switch (d)
        {
            case Span span:
                Fill(span.Inlines, text);
                break;
            case System.Windows.Controls.TextBlock tb:
                Fill(tb.Inlines, text);
                break;
        }
    }

    private static void Fill(InlineCollection inlines, string text)
    {
        inlines.Clear();
        if (text.Length == 0) return;
        // 絵文字が含まれない（ほとんどの）テキストは正規表現を通さずにそのまま
        if (!ContainsEmojiStart(text))
        {
            inlines.Add(new Run(text));
            return;
        }
        var pos = 0;
        foreach (Match m in EmojiData.MatchOne.Matches(text))
        {
            if (m.Index > pos) inlines.Add(new Run(text[pos..m.Index]));
            inlines.Add(new EmojiInline { Text = m.Value });
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) inlines.Add(new Run(text[pos..]));
    }

    private static bool ContainsEmojiStart(string text)
    {
        var starts = EmojiData.MatchStart;
        foreach (var ch in text)
            if (starts.Contains(ch)) return true;
        return false;
    }
}
