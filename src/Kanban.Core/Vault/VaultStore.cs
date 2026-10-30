using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kanban.Core.Models;

namespace Kanban.Core.Vault;

public sealed class VaultData
{
    public List<Project> Projects { get; } = new();
    public List<Board> Boards { get; } = new();
    public List<Tag> Tags { get; } = new();
    public List<Item> Items { get; } = new();
    public List<Comment> Comments { get; } = new();
    public List<ActivityLog> Activity { get; } = new();
    public long LastNumber { get; set; }
}

public static class VaultJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly JsonSerializerOptions LineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>
/// Reads/writes the vault as plain files. The vault is the source of truth;
/// callers treat this class as a durable, atomic key-value store.
/// </summary>
public sealed class VaultStore
{
    private readonly VaultPaths _paths;
    private readonly object _gate = new();

    /// <summary>
    /// 自分で書き込んだパスと時刻。ファイル監視が自分の書き込みを外部変更と誤認して
    /// 全件リロードしないよう、直後のイベントを無視するのに使う。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _ownWrites =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan OwnWriteWindow = TimeSpan.FromSeconds(3);

    private long _writeVersion;

    /// <summary>このプロセスが書き込む（消す）たびに増える。ロック外での読み込み中に書き込みがあったかの判定に使う。</summary>
    public long WriteVersion => Interlocked.Read(ref _writeVersion);

    private void MarkOwn(string path)
    {
        Interlocked.Increment(ref _writeVersion);
        _ownWrites[Path.GetFullPath(path)] = DateTime.UtcNow;
    }

    /// <summary>直近（数秒以内）にこのプロセスが書いた／消したパス（またはその親フォルダ）なら true。</summary>
    public bool IsOwnWrite(string path)
    {
        var now = DateTime.UtcNow;
        var full = Path.GetFullPath(path);
        for (var p = full; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
        {
            if (_ownWrites.TryGetValue(p, out var t) && now - t < OwnWriteWindow) return true;
            if (p.Length <= _paths.Root.Length) break;
        }
        return false;
    }

    public VaultStore(string root) : this(new VaultPaths(root)) { }
    public VaultStore(VaultPaths paths) => _paths = paths;

    public VaultPaths Paths => _paths;

    // ---- load ----

    public VaultData Load()
    {
        lock (_gate)
        {
            _paths.EnsureStructure();
            EnsureMeta();

            var data = new VaultData();

            var projects = ReadJson<ProjectFile>(_paths.ProjectsJson)?.Projects;
            if (projects is not null) data.Projects.AddRange(projects);

            var boards = ReadJson<BoardFile>(_paths.BoardsJson)?.Boards;
            if (boards is not null) data.Boards.AddRange(boards);

            var tags = ReadJson<TagFile>(_paths.TagsJson)?.Tags;
            if (tags is not null) data.Tags.AddRange(tags);

            var numberToItemId = new Dictionary<long, Guid>();
            if (Directory.Exists(_paths.ItemsDir))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.ItemsDir, "*.md"))
                {
                    var item = ParseItemFile(file);
                    if (item is null) continue;
                    data.Items.Add(item);
                    numberToItemId[item.Number] = item.Id;
                }
            }

            if (Directory.Exists(_paths.CommentsDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(_paths.CommentsDir))
                {
                    if (!long.TryParse(Path.GetFileName(dir), out var number)) continue;
                    if (!numberToItemId.TryGetValue(number, out var itemId)) continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
                    {
                        var c = ParseCommentFile(file, itemId);
                        if (c is not null) data.Comments.Add(c);
                    }
                }
            }

            if (Directory.Exists(_paths.ActivityDir))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.ActivityDir, "*.ndjson").OrderBy(f => f))
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        try
                        {
                            var log = JsonSerializer.Deserialize<ActivityLog>(line, VaultJson.LineOptions);
                            if (log is not null) data.Activity.Add(log);
                        }
                        catch (JsonException) { /* skip malformed line */ }
                    }
                }
            }

            var seq = ReadJson<SequenceFile>(_paths.SequenceJson);
            data.LastNumber = seq?.LastItemNumber
                ?? (data.Items.Count > 0 ? data.Items.Max(i => i.Number) : 0);

            return data;
        }
    }

    // ---- meta / json files ----

    private void EnsureMeta()
    {
        if (!File.Exists(_paths.VaultJson))
        {
            AtomicWrite(_paths.VaultJson, JsonSerializer.Serialize(new VaultFile
            {
                VaultId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow,
            }, VaultJson.Options));
        }
        if (!File.Exists(_paths.ProjectsJson))
            AtomicWrite(_paths.ProjectsJson, JsonSerializer.Serialize(new ProjectFile(), VaultJson.Options));
        if (!File.Exists(_paths.BoardsJson))
            AtomicWrite(_paths.BoardsJson, JsonSerializer.Serialize(new BoardFile(), VaultJson.Options));
        if (!File.Exists(_paths.TagsJson))
            AtomicWrite(_paths.TagsJson, JsonSerializer.Serialize(new TagFile(), VaultJson.Options));
        if (!File.Exists(_paths.SequenceJson))
            AtomicWrite(_paths.SequenceJson, JsonSerializer.Serialize(new SequenceFile { LastItemNumber = 0 }, VaultJson.Options));
    }

    private static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), VaultJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveProjects(IEnumerable<Project> projects)
    {
        var file = new ProjectFile { Projects = projects.ToList() };
        AtomicWrite(_paths.ProjectsJson, JsonSerializer.Serialize(file, VaultJson.Options));
    }

    public void SaveBoards(IEnumerable<Board> boards)
    {
        var file = new BoardFile { Boards = boards.ToList() };
        AtomicWrite(_paths.BoardsJson, JsonSerializer.Serialize(file, VaultJson.Options));
    }

    public void SaveTags(IEnumerable<Tag> tags)
    {
        var file = new TagFile { Tags = tags.ToList() };
        AtomicWrite(_paths.TagsJson, JsonSerializer.Serialize(file, VaultJson.Options));
    }

    public void SaveSequence(long lastNumber)
    {
        AtomicWrite(_paths.SequenceJson, JsonSerializer.Serialize(new SequenceFile { LastItemNumber = lastNumber }, VaultJson.Options));
    }

    // ---- items ----

    public void SaveItem(Item item)
    {
        var entries = new List<KeyValuePair<string, object?>>
        {
            new("schema", 1),
            new("id", item.Id),
            new("number", item.Number),
            new("board", item.BoardId),
            new("title", item.Title),
            new("priority", item.Priority),
            new("due", item.DueDate),
            new("tags", item.TagNames),
            new("archived", item.Archived),
            new("is_template", item.IsTemplate),
            new("tracker_template", item.TrackerTemplateId),
            new("position", item.Position),
            new("done_at", item.DoneAt),
            new("created", item.CreatedAt),
            new("updated", item.UpdatedAt),
        };

        var subtaskMaps = item.Subtasks
            .OrderBy(s => s.Position)
            .Select(s => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>
            {
                ["id"] = s.Id,
                ["title"] = s.Title,
                ["done"] = s.Done,
                ["position"] = s.Position,
            })
            .ToList();
        entries.Add(new("subtasks", subtaskMaps));

        var text = FrontMatter.Compose(entries, item.Description);
        AtomicWrite(_paths.ItemFile(item.Number), text);
    }

    public void DeleteItemFile(Item item)
    {
        lock (_gate)
        {
            var p = _paths.ItemFile(item.Number);
            MarkOwn(p);
            if (File.Exists(p)) File.Delete(p);
            var cdir = _paths.CommentsForItemDir(item.Number);
            MarkOwn(cdir);
            if (Directory.Exists(cdir)) Directory.Delete(cdir, recursive: true);
        }
    }

    public static Item? ParseItemForWatcher(string path) => ParseItemFile(path);

    private static Item? ParseItemFile(string path)    {
        try
        {
            var text = File.ReadAllText(path);
            var (meta, body) = FrontMatter.Parse(text);
            var id = FrontMatter.Guid(meta, "id");
            var number = FrontMatter.Long(meta, "number");
            var board = FrontMatter.Guid(meta, "board");
            if (id is null || number <= 0 || board is null) return null;

            var item = new Item
            {
                Id = id.Value,
                Number = number,
                BoardId = board.Value,
                Title = FrontMatter.Str(meta, "title") ?? "",
                Description = string.IsNullOrEmpty(body) ? null : body.TrimEnd('\n'),
                Priority = FrontMatter.Str(meta, "priority") ?? Priorities.Normal,
                DueDate = FrontMatter.Str(meta, "due"),
                Archived = FrontMatter.Bool(meta, "archived"),
                IsTemplate = FrontMatter.Bool(meta, "is_template"),
                TrackerTemplateId = FrontMatter.Guid(meta, "tracker_template"),
                Position = FrontMatter.Dbl(meta, "position"),
                DoneAt = FrontMatter.DateTime(meta, "done_at"),
                CreatedAt = FrontMatter.DateTime(meta, "created") ?? DateTimeOffset.UtcNow,
                UpdatedAt = FrontMatter.DateTime(meta, "updated") ?? DateTimeOffset.UtcNow,
                TagNames = FrontMatter.StringList(meta, "tags"),
            };

            foreach (var sm in FrontMatter.MapList(meta, "subtasks"))
            {
                var sid = FrontMatter.Guid(sm, "id") ?? Guid.NewGuid();
                item.Subtasks.Add(new Subtask
                {
                    Id = sid,
                    Title = FrontMatter.Str(sm, "title") ?? "",
                    Done = FrontMatter.Bool(sm, "done"),
                    Position = FrontMatter.Dbl(sm, "position"),
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt,
                });
            }
            item.Subtasks.Sort((a, b) => a.Position.CompareTo(b.Position));
            return item;
        }
        catch (IOException) { return null; }
    }

    // ---- comments ----

    public void SaveComment(Comment c, long itemNumber)
    {
        var entries = new List<KeyValuePair<string, object?>>
        {
            new("id", c.Id),
            new("item", c.ItemId),
            new("author", c.Author),
            new("created", c.CreatedAt),
        };
        var text = FrontMatter.Compose(entries, c.Content);
        var dir = _paths.CommentsForItemDir(itemNumber);
        MarkOwn(dir);
        Directory.CreateDirectory(dir);
        var name = $"{c.CreatedAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}-{Sanitize(c.Author)}-{c.Id.ToString("N")[..8]}.md";
        AtomicWrite(Path.Combine(dir, name), text);
    }

    public void DeleteComment(Comment c, long itemNumber)
    {
        lock (_gate)
        {
            var dir = _paths.CommentsForItemDir(itemNumber);
            MarkOwn(dir);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.EnumerateFiles(dir, $"*-{c.Id.ToString("N")[..8]}.md"))
                File.Delete(file);
        }
    }

    private static Comment? ParseCommentFile(string path, Guid itemId)
    {
        try
        {
            var (meta, body) = FrontMatter.Parse(File.ReadAllText(path));
            var id = FrontMatter.Guid(meta, "id") ?? Guid.NewGuid();
            return new Comment
            {
                Id = id,
                ItemId = itemId,
                Author = FrontMatter.Str(meta, "author") ?? "user",
                Content = body.TrimEnd('\n'),
                CreatedAt = FrontMatter.DateTime(meta, "created") ?? DateTimeOffset.UtcNow,
            };
        }
        catch (IOException) { return null; }
    }

    // ---- activity ----

    public void AppendActivity(ActivityLog log)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_paths.ActivityDir);
            var line = JsonSerializer.Serialize(log, VaultJson.LineOptions);
            MarkOwn(_paths.ActivityFile(log.CreatedAt));
            File.AppendAllText(_paths.ActivityFile(log.CreatedAt), line + "\n", new UTF8Encoding(false));
        }
    }

    // ---- low level ----

    private void AtomicWrite(string path, string content)
    {
        MarkOwn(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path))
        {
            try { File.Replace(tmp, path, null, ignoreMetadataErrors: true); }
            catch (IOException) { File.Delete(path); File.Move(tmp, path); }
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static string Sanitize(string s)
    {
        var chars = s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray();
        var result = new string(chars);
        return result.Length == 0 ? "unknown" : result;
    }

    // ---- wrapper types (file schemas) ----

    private sealed class ProjectFile { public int Schema { get; set; } = 1; public List<Project> Projects { get; set; } = new(); }
    private sealed class BoardFile { public int Schema { get; set; } = 1; public List<Board> Boards { get; set; } = new(); }
    private sealed class TagFile { public int Schema { get; set; } = 1; public List<Tag> Tags { get; set; } = new(); }
    private sealed class SequenceFile { public long LastItemNumber { get; set; } public int Schema { get; set; } = 1; }
    private sealed class VaultFile { public int Schema { get; set; } = 1; public Guid VaultId { get; set; } public DateTimeOffset CreatedAt { get; set; } }
}
