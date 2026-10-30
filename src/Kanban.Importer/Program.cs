using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using Kanban.Core;
using Kanban.Core.Models;
using Kanban.Core.Vault;

// Usage:
//   Kanban.Importer <dumpDir> <vaultDir> [--dry-run]
// Reads the Supabase CSV export and writes a Kanban vault.

var argsList = args.ToList();
var dryRun = argsList.Remove("--dry-run");
if (argsList.Count < 2)
{
    Console.Error.WriteLine("usage: Kanban.Importer <dumpDir> <vaultDir> [--dry-run]");
    return 1;
}

var dumpDir = argsList[0];
var vaultDir = argsList[1];

if (!Directory.Exists(dumpDir))
{
    Console.Error.WriteLine($"dump dir not found: {dumpDir}");
    return 1;
}

Console.WriteLine($"import: {dumpDir} -> {vaultDir}");

var projects = ReadCsv(Path.Combine(dumpDir, "projects_rows.csv"));
var boards = ReadCsv(Path.Combine(dumpDir, "boards_rows.csv"));
var tags = ReadCsv(Path.Combine(dumpDir, "tags_rows.csv"));
var items = ReadCsv(Path.Combine(dumpDir, "items_rows.csv"));
var itemTags = ReadCsv(Path.Combine(dumpDir, "item_tags_rows.csv"));
var subtasks = ReadCsv(Path.Combine(dumpDir, "subtasks_rows.csv"));
var comments = ReadCsv(Path.Combine(dumpDir, "comments_rows.csv"));
var activity = ReadCsv(Path.Combine(dumpDir, "activity_logs_rows.csv"));

// ---- projects ----
var projectList = projects.Select(r => new Project
{
    Id = Guid.Parse(r["id"]!),
    Name = r["name"] ?? "",
    Description = Blank(r["description"]),
    Color = r["color"] ?? "#64748b",
    Position = Int(r["position"]),
    Archived = Bool(r["archived"]),
    CreatedAt = Ts(r["created_at"]),
    UpdatedAt = Ts(r["updated_at"]),
}).ToList();

// ---- boards ----
var boardList = boards.Select(r => new Board
{
    Id = Guid.Parse(r["id"]!),
    ProjectId = Guid.Parse(r["project_id"]!),
    Name = r["name"] ?? "",
    Description = Blank(r["description"]),
    Color = r["color"] ?? "#64748b",
    Position = Int(r["position"]),
    Archived = Bool(r["archived"]),
    ShowNumbering = Bool(r["show_numbering"]),
    IsDone = Bool(r["is_done"]),
    Layout = Blank(r["layout"]) ?? BoardLayouts.Scroll,
    CreatedAt = Ts(r["created_at"]),
    UpdatedAt = Ts(r["updated_at"]),
}).ToList();

// ---- tags ----
var tagList = tags.Select(r => new Tag
{
    Id = Guid.Parse(r["id"]!),
    Name = r["name"] ?? "",
    Color = r["color"] ?? "#94a3b8",
    Path = Blank(r["path"]),
    Description = Blank(r["description"]),
    Archived = Bool(r["archived"]),
    ParentTagId = r.TryGetValue("parent_tag_id", out var p) ? GuidOrNull(p) : null,
    Emoji = Blank(r.TryGetValue("emoji", out var e) ? e : null),
    Favorite = Bool(r.TryGetValue("favorite", out var f) ? f : null),
    CreatedAt = Ts(r["created_at"]),
}).ToList();

var tagNameById = tagList.ToDictionary(t => t.Id, t => t.Name);

// ---- item -> tag names ----
var tagIdsByItem = itemTags
    .GroupBy(r => r["item_id"]!)
    .ToDictionary(g => g.Key, g => g.Select(r => r["tag_id"]!).ToList());

// ---- subtasks grouped by item ----
var subtasksByItem = subtasks
    .GroupBy(r => r["item_id"]!)
    .ToDictionary(g => g.Key, g => g.Select(r => new Subtask
    {
        Id = Guid.Parse(r["id"]!),
        Title = r["title"] ?? "",
        Done = Bool(r["done"]),
        Position = Int(r["position"]),
        CreatedAt = Ts(r["created_at"]),
        UpdatedAt = Ts(r["updated_at"]),
    }).OrderBy(s => s.Position).ToList());

// ---- items ----
var itemList = items.Select(r =>
{
    var id = Guid.Parse(r["id"]!);
    var item = new Item
    {
        Id = id,
        Number = Long(r["number"]),
        BoardId = Guid.Parse(r["board_id"]!),
        Title = r["title"] ?? "",
        Description = Blank(r["description"]),
        Priority = Blank(r["priority"]) ?? Priorities.Normal,
        DueDate = Blank(r["due_date"]),
        Archived = Bool(r["archived"]),
        IsTemplate = Bool(r["is_template"]),
        TrackerTemplateId = GuidOrNull(r["tracker_template_id"]),
        DoneAt = TsOrNull(r["done_at"]),
        CreatedAt = Ts(r["created_at"]),
        UpdatedAt = Ts(r["updated_at"]),
    };
    if (subtasksByItem.TryGetValue(id.ToString(), out var st)) item.Subtasks = st;
    if (tagIdsByItem.TryGetValue(id.ToString(), out var tids))
        item.TagNames = tids
            .Select(t => Guid.TryParse(t, out var g) && tagNameById.TryGetValue(g, out var nm) ? nm : null)
            .Where(n => n is not null).Select(n => n!)
            .Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
    return item;
}).ToList();

// Normalize positions per board to sparse doubles (keeps first-move cheap).
var originalPos = items.ToDictionary(r => Guid.Parse(r["id"]!), r => Int(r["position"]));
foreach (var group in itemList.GroupBy(i => i.BoardId))
{
    var pos = 1000.0;
    foreach (var item in group.OrderBy(i => originalPos[i.Id]))
    {
        item.Position = pos;
        pos += 1000;
    }
}

// ---- comments (map item uuid -> item number) ----
var numberById = itemList.ToDictionary(i => i.Id, i => i.Number);
var commentList = comments
    .Where(r => numberById.ContainsKey(Guid.Parse(r["item_id"]!)))
    .Select(r => new Comment
    {
        Id = Guid.Parse(r["id"]!),
        ItemId = Guid.Parse(r["item_id"]!),
        Author = Blank(r["author"]) ?? "user",
        Content = r["content"] ?? "",
        CreatedAt = Ts(r["created_at"]),
    }).ToList();

// ---- activity ----
var activityList = activity.Select(r => new ActivityLog
{
    Id = Long(r["id"]),
    Actor = Blank(r["actor"]) ?? "user",
    Action = r["action"] ?? "",
    TargetType = r["target_type"] ?? "",
    TargetId = GuidOrNull(r["target_id"]),
    BoardId = GuidOrNull(r["board_id"]),
    ItemId = GuidOrNull(r["item_id"]),
    Details = ParseJson(r["details"]),
    CreatedAt = Ts(r["created_at"]),
}).ToList();

var lastNumber = itemList.Count > 0 ? itemList.Max(i => i.Number) : 0;

Console.WriteLine($"parsed: projects={projectList.Count} boards={boardList.Count} tags={tagList.Count} " +
                  $"items={itemList.Count} subtasks={subtaskListCount(subtasksByItem)} comments={commentList.Count} activity={activityList.Count}");

if (!dryRun)
{
    var store = new VaultStore(vaultDir);
    store.Load(); // ensure structure/meta
    store.SaveProjects(projectList);
    store.SaveBoards(boardList);
    store.SaveTags(tagList);
    foreach (var item in itemList) store.SaveItem(item);
    foreach (var c in commentList)
    {
        if (numberById.TryGetValue(c.ItemId, out var num)) store.SaveComment(c, num);
    }
    foreach (var a in activityList) store.AppendActivity(a);
    store.SaveSequence(lastNumber);

    var maxAct = activityList.Count > 0 ? activityList.Max(a => a.Id) : 0;
    store.AppendActivity(new ActivityLog
    {
        Id = maxAct + 1,
        Actor = "importer",
        Action = "import.items",
        TargetType = "item",
        Details = JsonSerializer.SerializeToElement(new { count = itemList.Count }),
        CreatedAt = DateTimeOffset.UtcNow,
    });

    Console.WriteLine($"wrote vault: {vaultDir}");
}

Console.WriteLine("done.");
return 0;

// ---------------------------------------------------------------------------

static List<Dictionary<string, string?>> ReadCsv(string path)
{
    if (!File.Exists(path)) throw new FileNotFoundException(path);
    using var reader = new StreamReader(path);
    using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
    csv.Read();
    csv.ReadHeader();
    var headers = csv.HeaderRecord ?? Array.Empty<string>();
    var rows = new List<Dictionary<string, string?>>();
    while (csv.Read())
    {
        var row = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var h in headers) row[h] = csv.GetField(h);
        rows.Add(row);
    }
    return rows;
}

static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

static bool Bool(string? s) => string.Equals(s, "true", StringComparison.OrdinalIgnoreCase);

static int Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

static long Long(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

static Guid? GuidOrNull(string? s) => Guid.TryParse(s, out var g) ? g : null;

static DateTimeOffset Ts(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return DateTimeOffset.UtcNow;
    return TsOrNull(s) ?? DateTimeOffset.UtcNow;
}

static DateTimeOffset? TsOrNull(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return null;
    var v = Regex.Replace(s.Trim(), @"[+-]\d{2}$", m => m.Value + ":00");
    return DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}

static JsonElement? ParseJson(string? s)
{
    if (string.IsNullOrWhiteSpace(s)) return null;
    try
    {
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }
    catch (JsonException) { return null; }
}

static int subtaskListCount(Dictionary<string, List<Subtask>> map) => map.Values.Sum(v => v.Count);
