namespace Kanban.Core;

/// <summary>
/// タグ名からの自動配色。Next.js 版 lib/tag-palette.ts と同じ 16 色
/// （Tailwind *-500 をビット反転順に並べたもの）と FNV-1a + Murmur3 fmix32 ハッシュ。
/// </summary>
public static class TagPalette
{
    public static readonly IReadOnlyList<string> Colors = new[]
    {
        "#ef4444", "#06b6d4", "#84cc16", "#8b5cf6",
        "#f59e0b", "#3b82f6", "#10b981", "#d946ef",
        "#f97316", "#0ea5e9", "#22c55e", "#a855f7",
        "#eab308", "#6366f1", "#14b8a6", "#ec4899",
    };

    public static string Pick(string name)
    {
        unchecked
        {
            uint h = 0x811c9dc5;
            foreach (var ch in name)
            {
                h ^= ch;
                h *= 0x01000193;
            }
            h ^= h >> 16;
            h *= 0x85ebca6b;
            h ^= h >> 13;
            h *= 0xc2b2ae35;
            h ^= h >> 16;
            return Colors[(int)(h % (uint)Colors.Count)];
        }
    }
}
