using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Kanban.Desktop;

/// <summary>
/// 最小限の Markdown プレビュー描画（読み取り専用 RichTextBox の FlowDocument に出力。文字は選択・コピーできる）。
/// 対応: 見出し (#/##/###)、箇条書き (- / * 、字下げでネスト)、チェックボックス (- [ ] / - [x])、引用 (>)、
/// コードブロック (```)、表 (| a | b |)、**太字**、*斜体*、`コード`、[ラベル](url)、水平線 (---)。
/// それ以外はプレーンテキストとして流す。完全な Markdown ではなく、コメント表示用の軽量プレビュー。
/// XAML からは RichTextBox に <c>local:MarkdownMini.Source</c> 添付プロパティを付けて使う。
/// </summary>
public static class MarkdownMini
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(MarkdownMini),
        new PropertyMetadata(null, (d, e) =>
        {
            if (d is not RichTextBox rtb) return;
            if (rtb.ReadLocalValue(WheelHookedProperty) == DependencyProperty.UnsetValue)
            {
                rtb.SetValue(WheelHookedProperty, true);
                rtb.PreviewMouseWheel += ForwardWheel;
                rtb.SizeChanged += (_, _) => LayoutTables(rtb);
            }
            rtb.Document = BuildDocument(e.NewValue as string);
            LayoutTables(rtb);
        }));

    /// <summary>
    /// 表の列幅を RichTextBox の実際の幅からピクセルで決める。FlowDocument の Table に * 指定の列幅を渡すと
    /// 表が欄より広く組まれ、右側の列が極端に細く（1 文字ずつ折り返して縦に伸び）はみ出すため。
    /// 各列は中身の長さに応じた比率で配分し、最低幅を確保してから全体を欄の幅に収める。
    /// </summary>
    private static void LayoutTables(RichTextBox rtb)
    {
        // 内側の余白（RichTextBox の Padding・枠、文書の内部余白）を引いた、文書に使える幅
        var avail = rtb.ActualWidth - rtb.Padding.Left - rtb.Padding.Right
                    - rtb.BorderThickness.Left - rtb.BorderThickness.Right - 12;
        if (avail <= 0) return;
        foreach (var table in rtb.Document.Blocks.OfType<Table>())
        {
            if (table.Tag is not double[] weights) continue;
            const double minCol = 64;
            var total = weights.Sum();
            var widths = weights.Select(w => Math.Max(minCol, avail * w / total)).ToArray();
            // 最低幅を確保した分あふれたら、全体を縮めて欄の幅に収める（はみ出しはさせない）
            var scale = Math.Min(1, avail / widths.Sum());
            for (var c = 0; c < table.Columns.Count && c < widths.Length; c++)
                table.Columns[c].Width = new GridLength(Math.Floor(widths[c] * scale));
        }
    }

    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, string? value) => d.SetValue(SourceProperty, value);

    private static readonly DependencyProperty WheelHookedProperty = DependencyProperty.RegisterAttached(
        "WheelHooked", typeof(bool), typeof(MarkdownMini));

    /// <summary>
    /// RichTextBox 内部の ScrollViewer はスクロールする中身が無くてもホイールを消費するので、
    /// 外側（詳細ウィンドウ）の ScrollViewer へそのまま回す。
    /// </summary>
    private static void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement el || VisualTreeHelper.GetParent(el) is not UIElement parent) return;
        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }

    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    private static readonly Brush Muted = Freeze(new SolidColorBrush(Color.FromRgb(0x73, 0x73, 0x73)));
    private static readonly Brush CodeBg = Freeze(new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9)));
    private static readonly Brush LinkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Brush RuleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5)));
    private static readonly Brush TableBorder = RuleBrush;
    private static readonly Brush TableHeaderBg = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5)));

    private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>文章は段落（行は改行でつなぐ）、表は Table にして FlowDocument を組み立てる。</summary>
    public static FlowDocument BuildDocument(string? markdown)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(new Paragraph(new Run("（本文なし）") { Foreground = Muted }) { Margin = new Thickness(0) });
            return doc;
        }

        var lines = markdown.TrimEnd().Replace("\r\n", "\n").Split('\n');
        var text = new List<string>();
        var inCode = false;

        void FlushText()
        {
            // 表の前後の空行は余白で表すので、端の空行は落とす
            while (text.Count > 0 && string.IsNullOrWhiteSpace(text[0])) text.RemoveAt(0);
            while (text.Count > 0 && string.IsNullOrWhiteSpace(text[^1])) text.RemoveAt(text.Count - 1);
            if (text.Count > 0) doc.Blocks.Add(BuildParagraph(text));
            text.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```")) inCode = !inCode;
            // 表: 「| ... |」の行の次が区切り行（|---|:--:|）なら、続く「|」始まりの行までを表とみなす
            if (!inCode && IsTableRow(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                var rows = new List<List<string>> { SplitRow(line) };
                i += 2;
                while (i < lines.Length && IsTableRow(lines[i])) rows.Add(SplitRow(lines[i++]));
                i--;
                FlushText();
                doc.Blocks.Add(BuildTable(rows));
                continue;
            }
            text.Add(line);
        }
        FlushText();
        return doc;
    }

    private static Paragraph BuildParagraph(List<string> lines)
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        var first = true;
        var inCode = false;
        foreach (var raw in lines)
        {
            // コードブロックの囲み行は出さず、中身は等幅・背景付きでそのまま出す
            if (raw.TrimStart().StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            if (!first) p.Inlines.Add(new LineBreak());
            first = false;
            if (inCode)
            {
                p.Inlines.Add(new Run(raw.Length == 0 ? " " : raw) { FontFamily = MonoFont, FontSize = 12, Background = CodeBg });
                continue;
            }

            // 字下げはそのまま左余白として残す（ネストした箇条書き）
            var line = raw.TrimStart(' ', '\t');
            var indent = raw.Length - line.Length;
            double size = 12.5;
            var bold = false;
            var prefix = indent > 0 ? new string(' ', indent * 2) : "";

            if (line.StartsWith("- [ ] ") || line.StartsWith("* [ ] ")) { prefix += "☐  "; line = line[6..]; }
            else if (line.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase)
                     || line.StartsWith("* [x] ", StringComparison.OrdinalIgnoreCase)) { prefix += "☑  "; line = line[6..]; }
            else if (line.StartsWith("#### ")) { size = 12.5; bold = true; line = line[5..]; }
            else if (line.StartsWith("### ")) { size = 13; bold = true; line = line[4..]; }
            else if (line.StartsWith("## ")) { size = 14.5; bold = true; line = line[3..]; }
            else if (line.StartsWith("# ")) { size = 16; bold = true; line = line[2..]; }
            else if (line.StartsWith("- ") || line.StartsWith("* ")) { prefix += "•  "; line = line[2..]; }
            else if (line.StartsWith("> ")) { prefix += "▎ "; line = line[2..]; }

            if (indent == 0 && line.Trim() is "---" or "***")
            {
                p.Inlines.Add(new Run(new string('─', 40)) { Foreground = RuleBrush, FontSize = 10 });
                continue;
            }

            if (prefix.Length > 0)
                p.Inlines.Add(new Run(prefix) { Foreground = Muted });
            foreach (var inline in ParseInline(line, size, bold))
                p.Inlines.Add(inline);
        }
        return p;
    }

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith('|');

    private static bool IsTableSeparator(string line)
    {
        var t = line.Trim();
        return t.StartsWith('|') && t.Contains('-') && t.All(c => c is '|' or '-' or ':' or ' ' or '\t');
    }

    /// <summary>行をセルに分ける。`コード` の中と \| の「|」は区切りにしない。</summary>
    private static List<string> SplitRow(string line)
    {
        var t = line.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|")) t = t[..^1];
        var cells = new List<string>();
        var sb = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            if (c == '\\' && i + 1 < t.Length && t[i + 1] == '|') { sb.Append('|'); i++; continue; }
            if (c == '`') inCode = !inCode;
            if (c == '|' && !inCode) { cells.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(c);
        }
        cells.Add(sb.ToString().Trim());
        return cells;
    }

    private static Table BuildTable(List<List<string>> rows)
    {
        var cols = rows.Max(r => r.Count);
        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = TableBorder,
            // 隣り合う線が二重にならないよう、外枠は左と上、セルは右と下だけ引く
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 6, 0, 6),
        };
        // 列幅の比率: 各列の最長セルの長さの平方根（長い列が他の列を押しつぶさないよう差をならす）。
        // 実際のピクセル幅は欄の幅が決まってから LayoutTables で設定する
        var weights = new double[cols];
        for (var c = 0; c < cols; c++)
        {
            var len = rows.Max(r => c < r.Count ? r[c].Length : 0);
            weights[c] = Math.Sqrt(Math.Clamp(len, 4, 80));
            table.Columns.Add(new TableColumn());
        }
        table.Tag = weights;
        var group = new TableRowGroup();
        for (var r = 0; r < rows.Count; r++)
        {
            var row = new TableRow();
            if (r == 0) row.Background = TableHeaderBg;
            for (var c = 0; c < cols; c++)
            {
                var para = new Paragraph { Margin = new Thickness(0) };
                foreach (var inline in ParseInline(c < rows[r].Count ? rows[r][c] : "", 12, forceBold: r == 0))
                    para.Inlines.Add(inline);
                row.Cells.Add(new TableCell(para)
                {
                    Padding = new Thickness(6, 3, 6, 3),
                    BorderBrush = TableBorder,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                });
            }
            group.Rows.Add(row);
        }
        table.RowGroups.Add(group);
        return table;
    }

    /// <summary>**太字** / *斜体* / `コード` / [ラベル](url) をスキャンして Inline 列にする。</summary>
    private static IEnumerable<Inline> ParseInline(string text, double size, bool forceBold)
    {
        var plain = new StringBuilder();
        var weight = forceBold ? FontWeights.SemiBold : FontWeights.Normal;
        var i = 0;

        IEnumerable<Inline> Flush()
        {
            if (plain.Length == 0) yield break;
            yield return new Run(plain.ToString()) { FontSize = size, FontWeight = weight };
            plain.Clear();
        }

        while (i < text.Length)
        {
            if (text.AsSpan(i).StartsWith("**"))
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    foreach (var r in Flush()) yield return r;
                    // 太字の中の `コード` なども解釈する
                    foreach (var r in ParseInline(text[(i + 2)..end], size, forceBold: true)) yield return r;
                    i = end + 2;
                    continue;
                }
            }
            // 「a * b」のような掛け算・記号を斜体にしないよう、直後が空白なら対象外
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] != ' ')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    foreach (var r in Flush()) yield return r;
                    yield return new Run(text[(i + 1)..end]) { FontSize = size, FontWeight = weight, FontStyle = FontStyles.Italic };
                    i = end + 1;
                    continue;
                }
            }
            if (text[i] == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    foreach (var r in Flush()) yield return r;
                    yield return new Run(text[(i + 1)..end])
                    {
                        FontSize = size - 0.5,
                        FontWeight = weight,
                        FontFamily = MonoFont,
                        Background = CodeBg,
                    };
                    i = end + 1;
                    continue;
                }
            }
            if (text[i] == '[')
            {
                var closeBracket = text.IndexOf(']', i + 1);
                if (closeBracket > i + 1
                    && closeBracket + 1 < text.Length && text[closeBracket + 1] == '(')
                {
                    var closeParen = text.IndexOf(')', closeBracket + 2);
                    if (closeParen > closeBracket + 2)
                    {
                        foreach (var r in Flush()) yield return r;
                        yield return new Run(text[(i + 1)..closeBracket])
                        {
                            FontSize = size,
                            FontWeight = weight,
                            Foreground = LinkBrush,
                            TextDecorations = System.Windows.TextDecorations.Underline,
                        };
                        i = closeParen + 1;
                        continue;
                    }
                }
            }
            plain.Append(text[i]);
            i++;
        }
        foreach (var r in Flush()) yield return r;
    }
}
