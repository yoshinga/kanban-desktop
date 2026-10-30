using System.Globalization;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace Kanban.Core.Vault;

/// <summary>
/// Reads YAML front matter (via YamlDotNet) and writes it with a small,
/// deterministic emitter so key order stays stable and human friendly.
/// Only the subset we need is supported on write: scalars, flow lists of
/// scalars, and block lists of inline maps.
/// </summary>
public static class FrontMatter
{
    public static (YamlMappingNode Meta, string Body) Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return (new YamlMappingNode(), normalized);

        var end = normalized.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0)
            return (new YamlMappingNode(), normalized);

        var yamlText = normalized.Substring(4, end - 4);
        var afterMarkerStart = normalized.IndexOf('\n', end + 1);
        var body = afterMarkerStart < 0 ? "" : normalized.Substring(afterMarkerStart + 1);

        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yamlText));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return (new YamlMappingNode(), normalized);
        }

        var root = stream.Documents.Count > 0
            ? stream.Documents[0].RootNode as YamlMappingNode ?? new YamlMappingNode()
            : new YamlMappingNode();
        return (root, body);
    }

    // ---- read helpers ----

    private static bool TryGet(YamlMappingNode m, string key, out YamlNode node)
    {
        foreach (var kv in m.Children)
        {
            if (kv.Key is YamlScalarNode s && s.Value == key)
            {
                node = kv.Value;
                return true;
            }
        }
        node = new YamlScalarNode();
        return false;
    }

    private static string? ScalarValue(YamlNode node)
    {
        if (node is not YamlScalarNode s) return null;
        var v = s.Value;
        if (string.IsNullOrEmpty(v) || v == "~" || v == "null") return null;
        return v;
    }

    public static string? Str(YamlMappingNode m, string key)
        => TryGet(m, key, out var n) ? ScalarValue(n) : null;

    public static bool Bool(YamlMappingNode m, string key, bool fallback = false)
    {
        var v = Str(m, key);
        return v is null ? fallback : v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public static int Int(YamlMappingNode m, string key, int fallback = 0)
    {
        var v = Str(m, key);
        return v is not null && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i : fallback;
    }

    public static long Long(YamlMappingNode m, string key, long fallback = 0)
    {
        var v = Str(m, key);
        return v is not null && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i : fallback;
    }

    public static double Dbl(YamlMappingNode m, string key, double fallback = 0)
    {
        var v = Str(m, key);
        return v is not null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : fallback;
    }

    public static DateTimeOffset? DateTime(YamlMappingNode m, string key)
    {
        var v = Str(m, key);
        return v is not null && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d)
            ? d : null;
    }

    public static Guid? Guid(YamlMappingNode m, string key)
    {
        var v = Str(m, key);
        return v is not null && System.Guid.TryParse(v, out var g) ? g : null;
    }

    public static List<string> StringList(YamlMappingNode m, string key)
    {
        var result = new List<string>();
        if (!TryGet(m, key, out var node)) return result;
        if (node is YamlSequenceNode seq)
        {
            foreach (var child in seq.Children)
            {
                var v = ScalarValue(child);
                if (!string.IsNullOrEmpty(v)) result.Add(v);
            }
        }
        else
        {
            var v = ScalarValue(node);
            if (!string.IsNullOrEmpty(v)) result.Add(v);
        }
        return result;
    }

    public static List<YamlMappingNode> MapList(YamlMappingNode m, string key)
    {
        var result = new List<YamlMappingNode>();
        if (TryGet(m, key, out var node) && node is YamlSequenceNode seq)
        {
            foreach (var child in seq.Children)
                if (child is YamlMappingNode map) result.Add(map);
        }
        return result;
    }

    // ---- write ----

    /// <summary>Render a front-matter document from ordered entries plus body.</summary>
    public static string Compose(IEnumerable<KeyValuePair<string, object?>> entries, string? body)
    {
        var sb = new StringBuilder();
        sb.Append("---\n");
        foreach (var (key, value) in entries)
            WriteEntry(sb, key, value);
        sb.Append("---\n");
        var b = body ?? "";
        if (b.Length > 0)
        {
            sb.Append(b);
            if (!b.EndsWith('\n')) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static void WriteEntry(StringBuilder sb, string key, object? value)
    {
        switch (value)
        {
            case null:
                sb.Append(key).Append(":\n");
                break;
            case bool b:
                sb.Append(key).Append(": ").Append(b ? "true" : "false").Append('\n');
                break;
            case int i:
                sb.Append(key).Append(": ").Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
                break;
            case double dbl:
                sb.Append(key).Append(": ").Append(dbl.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                break;
            case long l:
                sb.Append(key).Append(": ").Append(l.ToString(CultureInfo.InvariantCulture)).Append('\n');
                break;
            case DateTimeOffset d:
                sb.Append(key).Append(": ").Append(Quote(d.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture))).Append('\n');
                break;
            case Guid g:
                sb.Append(key).Append(": ").Append(g.ToString()).Append('\n');
                break;
            case string s:
                sb.Append(key).Append(": ").Append(Quote(s)).Append('\n');
                break;
            case IEnumerable<string> list:
                WriteFlowList(sb, key, list);
                break;
            case IEnumerable<IReadOnlyDictionary<string, object?>> maps:
                WriteMapList(sb, key, maps);
                break;
            default:
                sb.Append(key).Append(": ").Append(Quote(value.ToString() ?? "")).Append('\n');
                break;
        }
    }

    private static void WriteFlowList(StringBuilder sb, string key, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
        {
            sb.Append(key).Append(": []\n");
            return;
        }
        sb.Append(key).Append(": [")
          .Append(string.Join(", ", list.Select(Quote)))
          .Append("]\n");
    }

    private static void WriteMapList(StringBuilder sb, string key, IEnumerable<IReadOnlyDictionary<string, object?>> maps)
    {
        var list = maps.ToList();
        if (list.Count == 0)
        {
            sb.Append(key).Append(": []\n");
            return;
        }
        sb.Append(key).Append(":\n");
        foreach (var map in list)
        {
            sb.Append("  - {");
            var first = true;
            foreach (var kv in map)
            {
                if (!first) sb.Append(", ");
                first = false;
                sb.Append(kv.Key).Append(": ").Append(FormatInline(kv.Value));
            }
            sb.Append("}\n");
        }
    }

    private static string FormatInline(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double dbl => dbl.ToString("R", CultureInfo.InvariantCulture),
        Guid g => g.ToString(),
        _ => Quote(value.ToString() ?? ""),
    };

    /// <summary>Plain when safe, double-quoted otherwise.</summary>
    public static string Quote(string s)
    {
        if (s.Length == 0) return "\"\"";
        if (!NeedsQuote(s)) return s;
        var escaped = s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                       .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        return "\"" + escaped + "\"";
    }

    private static bool NeedsQuote(string s)
    {
        if (s != s.Trim()) return true;
        if (s is "true" or "false" or "null" or "~" or "yes" or "no" or "on" or "off") return true;
        if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return true;
        foreach (var c in s)
        {
            if (c is ':' or '#' or '{' or '}' or '[' or ']' or ',' or '&' or '*' or '!' or '|'
                or '>' or '\'' or '"' or '%' or '@' or '`' or '\n' or '\r' or '\t')
                return true;
        }
        if (s[0] is '-' or '?' or ' ') return true;
        return false;
    }
}
