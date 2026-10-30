using System.Text.Json;
using System.Text.RegularExpressions;
using Kanban.Core.Models;
using Kanban.Core.Vault;

namespace Kanban.Core;

/// <summary>
/// In-memory projection of the vault with domain rules (numbering, positions,
/// done_at, activity log). All changes are persisted to the vault as plain
/// files. The vault is the source of truth; this service is the single writer.
/// </summary>
public sealed class KanbanService
{
    private static readonly Regex DueDateRe = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled);

    private readonly VaultStore _store;
    private readonly object _gate = new();

    private readonly Dictionary<Guid, Project> _projects = new();
    private readonly Dictionary<Guid, Board> _boards = new();
    private readonly Dictionary<Guid, Tag> _tags = new();
    private readonly Dictionary<string, Tag> _tagsByName = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Item> _items = new();
    private readonly Dictionary<long, Guid> _itemIdByNumber = new();
    private readonly Dictionary<Guid, List<Comment>> _comments = new();
    private readonly List<ActivityLog> _activity = new();
    private long _lastNumber;
    private long _lastActivityId;

    public event EventHandler<VaultChangedEventArgs>? Changed;

    public VaultPaths Paths => _store.Paths;

    public KanbanService(string root) : this(new VaultStore(root)) { }

    public KanbanService(VaultStore store)
    {
        _store = store;
        LoadFromStore();
    }

    // ------------------------------------------------------------------
    // Loading / reload
    // ------------------------------------------------------------------

    private void LoadFromStore() => Apply(_store.Load());

    private void Apply(VaultData data)
    {
        _projects.Clear(); _boards.Clear(); _tags.Clear(); _items.Clear();
        _itemIdByNumber.Clear(); _comments.Clear(); _activity.Clear();

        foreach (var p in data.Projects) _projects[p.Id] = p;
        foreach (var b in data.Boards) _boards[b.Id] = b;
        foreach (var t in data.Tags) _tags[t.Id] = t;
        foreach (var i in data.Items)
        {
            _items[i.Id] = i;
            _itemIdByNumber[i.Number] = i.Id;
        }
        foreach (var c in data.Comments)
        {
            if (!_comments.TryGetValue(c.ItemId, out var list))
                _comments[c.ItemId] = list = new List<Comment>();
            list.Add(c);
        }
        foreach (var list in _comments.Values)
            list.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        _activity.AddRange(data.Activity.OrderBy(a => a.Id).ThenBy(a => a.CreatedAt));
        _lastNumber = data.LastNumber;
        _lastActivityId = _activity.Count > 0 ? _activity.Max(a => a.Id) : 0;
        RebuildTagIndex();
    }

    private void RebuildTagIndex()
    {
        _tagsByName.Clear();
        foreach (var t in _tags.Values) _tagsByName[t.Name] = t;
    }

    private bool EnsureTagsExist(IEnumerable<string> names)
    {
        var created = false;
        foreach (var n in names)
        {
            if (_tagsByName.ContainsKey(n)) continue;
            var t = new Tag { Name = n, Color = TagPalette.Pick(n) };
            _tags[t.Id] = t;
            _tagsByName[n] = t;
            created = true;
        }
        return created;
    }

    /// <summary>Reload everything from disk (used by the file watcher).</summary>
    /// <remarks>
    /// 全件の読み込みは時間がかかる（1000 件超で 0.5 秒前後）ので、ロックの外で読んで差し替えだけをロック内で行う。
    /// ロック内で読むと、その間 UI からの操作（ドロップでの移動など）が待たされて画面が固まる。
    /// 読んでいる間にこのプロセスが書き込んだら、読んだ内容は古い可能性があるので読み直す。
    /// </remarks>
    public void ReloadAll()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            long version;
            // 書き込みはすべて _gate の中で行うので、ここで一度取れば書きかけの状態は読まない
            lock (_gate) version = _store.WriteVersion;
            var data = _store.Load();
            lock (_gate)
            {
                if (_store.WriteVersion != version) continue;
                Apply(data);
            }
            Notify();
            return;
        }
        lock (_gate) LoadFromStore();
        Notify();
    }

    /// <summary>Reload a single item file (cheap path for external edits).</summary>
    public bool ReloadItem(long number)
    {
        bool changed;
        Guid? boardId = null;
        lock (_gate)
        {
            var path = _store.Paths.ItemFile(number);
            if (!File.Exists(path))
            {
                if (_itemIdByNumber.TryGetValue(number, out var dead))
                {
                    boardId = _items.TryGetValue(dead, out var it) ? it.BoardId : null;
                    _items.Remove(dead);
                    _itemIdByNumber.Remove(number);
                    _comments.Remove(dead);
                    changed = true;
                }
                else changed = false;
            }
            else
            {
                var parsed = VaultStore.ParseItemForWatcher(path);
                if (parsed is null) changed = false;
                else
                {
                    boardId = parsed.BoardId;
                    _items[parsed.Id] = parsed;
                    _itemIdByNumber[parsed.Number] = parsed.Id;
                    changed = true;
                }
            }
        }
        if (changed) Notify(boardId.HasValue ? new[] { boardId.Value } : Array.Empty<Guid>());
        return changed;
    }

    // ------------------------------------------------------------------
    // Projects
    // ------------------------------------------------------------------

    public List<Project> ListProjects(bool includeArchived = false)
    {
        lock (_gate)
            return _projects.Values
                .Where(p => includeArchived || !p.Archived)
                .OrderBy(p => p.Position).ThenBy(p => p.CreatedAt)
                .ToList();
    }

    public Project GetProject(Guid id)
    {
        lock (_gate)
            return _projects.TryGetValue(id, out var p) ? p : throw new NotFoundException("Project not found");
    }

    public Project CreateProject(ProjectCreate input, string actor)
    {
        var name = Require(input.Name, "name");
        var project = new Project
        {
            Name = name,
            Description = input.Description,
            Color = string.IsNullOrWhiteSpace(input.Color) ? "#64748b" : input.Color!,
            Position = input.Position ?? NextProjectPosition(),
        };
        lock (_gate)
        {
            _projects[project.Id] = project;
            PersistProjects();
            Log(actor, "project.create", "project", project.Id, null, null, new { name = project.Name });
        }
        Notify();
        return project;
    }

    public Project UpdateProject(Guid id, JsonElement patch, string actor)
    {
        Project p;
        lock (_gate)
        {
            p = GetProject(id);
            foreach (var prop in patch.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "name": p.Name = Require(prop.Value.GetString(), "name"); break;
                    case "description": p.Description = ReadNullableString(prop.Value); break;
                    case "color": p.Color = prop.Value.GetString() ?? p.Color; break;
                    case "position": p.Position = prop.Value.GetInt32(); break;
                    case "archived": p.Archived = prop.Value.GetBoolean(); break;
                }
            }
            p.UpdatedAt = DateTimeOffset.UtcNow;
            PersistProjects();
            Log(actor, "project.update", "project", p.Id, null, null, new { });
        }
        Notify();
        return p;
    }

    public void DeleteProject(Guid id, string actor)
    {
        List<Guid> boardIds;
        lock (_gate)
        {
            if (!_projects.TryGetValue(id, out var p)) throw new NotFoundException("Project not found");
            boardIds = _boards.Values.Where(b => b.ProjectId == id).Select(b => b.Id).ToList();
            foreach (var b in boardIds) DeleteBoardCore(b);
            _projects.Remove(id);
            PersistProjects();
            PersistBoards();
            Log(actor, "project.delete", "project", id, null, null, new { name = p.Name });
        }
        Notify();
    }

    // ------------------------------------------------------------------
    // Boards
    // ------------------------------------------------------------------

    public List<Board> ListBoards(Guid? projectId = null, bool includeArchived = false)
    {
        lock (_gate)
            return _boards.Values
                .Where(b => (projectId is null || b.ProjectId == projectId) && (includeArchived || !b.Archived))
                .OrderBy(b => b.Position).ThenBy(b => b.CreatedAt)
                .ToList();
    }

    public Board GetBoard(Guid id)
    {
        lock (_gate)
            return _boards.TryGetValue(id, out var b) ? b : throw new NotFoundException("Board not found");
    }

    public Board CreateBoard(BoardCreate input, string actor)
    {
        var name = Require(input.Name, "name");
        lock (_gate)
        {
            if (!_projects.ContainsKey(input.ProjectId)) throw new NotFoundException("Project not found");
            var board = new Board
            {
                ProjectId = input.ProjectId,
                Name = name,
                Description = input.Description,
                Color = string.IsNullOrWhiteSpace(input.Color) ? "#64748b" : input.Color!,
                Position = input.Position ?? NextBoardPosition(input.ProjectId),
                ShowNumbering = input.ShowNumbering ?? false,
                IsDone = input.IsDone ?? false,
                Layout = BoardLayouts.IsValid(input.Layout) ? input.Layout! : BoardLayouts.Scroll,
                Emoji = string.IsNullOrWhiteSpace(input.Emoji) ? null : input.Emoji,
            };
            _boards[board.Id] = board;
            PersistBoards();
            Log(actor, "board.create", "board", board.Id, board.Id, null, new { name = board.Name });
            Notify(board.Id);
            return board;
        }
    }

    public Board UpdateBoard(Guid id, JsonElement patch, string actor)
    {
        Board b;
        lock (_gate)
        {
            b = GetBoard(id);
            foreach (var prop in patch.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "name": b.Name = Require(prop.Value.GetString(), "name"); break;
                    case "description": b.Description = ReadNullableString(prop.Value); break;
                    case "color": b.Color = prop.Value.GetString() ?? b.Color; break;
                    case "emoji": b.Emoji = ReadNullableString(prop.Value) is { Length: > 0 } e ? e : null; break;
                    case "position": b.Position = prop.Value.GetInt32(); break;
                    case "archived": b.Archived = prop.Value.GetBoolean(); break;
                    case "show_numbering": b.ShowNumbering = prop.Value.GetBoolean(); break;
                    case "is_done": b.IsDone = prop.Value.GetBoolean(); break;
                    case "layout":
                        var layout = prop.Value.GetString();
                        if (BoardLayouts.IsValid(layout)) b.Layout = layout!;
                        break;
                }
            }
            b.UpdatedAt = DateTimeOffset.UtcNow;
            PersistBoards();
            Log(actor, "board.update", "board", b.Id, b.Id, null, new { });
        }
        Notify(b.Id);
        return b;
    }

    public void DeleteBoard(Guid id, string actor)
    {
        lock (_gate)
        {
            if (!_boards.TryGetValue(id, out var b)) throw new NotFoundException("Board not found");
            DeleteBoardCore(id);
            PersistBoards();
            Log(actor, "board.delete", "board", id, id, null, new { name = b.Name });
        }
        Notify(id);
    }

    private void DeleteBoardCore(Guid id)
    {
        foreach (var item in _items.Values.Where(i => i.BoardId == id).ToList())
        {
            _store.DeleteItemFile(item);
            _items.Remove(item.Id);
            _itemIdByNumber.Remove(item.Number);
            _comments.Remove(item.Id);
        }
        _boards.Remove(id);
    }

    // ------------------------------------------------------------------
    // Items
    // ------------------------------------------------------------------

    public Item GetItem(string idOrNumber)
    {
        lock (_gate)
        {
            var item = ResolveItem(idOrNumber);
            item.Tags = ResolveTagsForItem(item);
            item.Comments = _comments.TryGetValue(item.Id, out var c) ? c.ToList() : new List<Comment>();
            return item;
        }
    }

    public ItemSummary Summarize(Item item)
    {
        var tags = ResolveTagsForItem(item);
        var comments = _comments.TryGetValue(item.Id, out var c) ? c.Count : 0;
        return new ItemSummary
        {
            Item = item,
            Tags = tags,
            SubtasksCount = (item.Subtasks.Count, item.Subtasks.Count(s => s.Done)),
            CommentsCount = comments,
        };
    }

    public BoardItemsResult ListItemsForBoard(
        Guid boardId,
        IReadOnlyCollection<string>? includes = null,
        bool includeArchived = false,
        int? limit = null,
        int? offset = null)
    {
        lock (_gate)
        {
            if (!_boards.TryGetValue(boardId, out var board)) throw new NotFoundException("Board not found");
            var all = _items.Values
                .Where(i => i.BoardId == boardId && !i.IsTemplate && (includeArchived || !i.Archived))
                .OrderBy(i => i.Position).ThenBy(i => i.CreatedAt)
                .ToList();
            var total = all.Count;
            var off = Math.Max(offset ?? 0, 0);
            var lim = limit is > 0 ? Math.Min(limit.Value, 200) : (int?)null;
            var page = lim is null ? all.Skip(off).ToList() : all.Skip(off).Take(lim.Value).ToList();
            var summaries = BuildSummaries(page, includes);
            return new BoardItemsResult(board, summaries, summaries.Count, total, lim, off,
                lim is not null && off + summaries.Count < total);
        }
    }

    public ProjectBoardItemsResult ListProjectBoardItems(Guid projectId, int inboxLimit = 200, int otherLimit = 50)
    {
        lock (_gate)
        {
            if (!_projects.ContainsKey(projectId)) throw new NotFoundException("Project not found");
            var boards = _boards.Values
                .Where(b => b.ProjectId == projectId && !b.Archived)
                .OrderBy(b => b.Position).ToList();

            var inboxId = boards.FirstOrDefault(b => b.Name == "Inbox")?.Id
                          ?? boards.FirstOrDefault()?.Id;
            var doingId = boards.FirstOrDefault(b => b.Name == "Doing")?.Id;

            var byBoard = new Dictionary<Guid, BoardItemsPage>();
            foreach (var b in boards)
            {
                var lim = (b.Id == inboxId || b.Id == doingId) ? inboxLimit : otherLimit;
                var all = _items.Values
                    .Where(i => i.BoardId == b.Id && !i.IsTemplate && !i.Archived)
                    .OrderBy(i => i.Position).ThenBy(i => i.CreatedAt).ToList();
                var page = all.Take(lim).ToList();
                var summaries = BuildSummaries(page, new[] { "tags", "subtasks_count", "comments_count" });
                byBoard[b.Id] = new BoardItemsPage(summaries, summaries.Count, all.Count, lim, 0,
                    summaries.Count < all.Count);
            }
            return new ProjectBoardItemsResult(boards, byBoard);
        }
    }

    /// <summary>
    /// List every item carrying a tag, across all boards, in one pass.
    /// Lets agents resolve a project/category tag without walking each board.
    /// </summary>
    public TagItemsResult ListItemsByTag(
        string tagName,
        Guid? projectId = null,
        bool includeArchived = false,
        int? limit = null)
    {
        tagName = Require(tagName, "tag");
        lock (_gate)
        {
            var tag = _tags.Values.FirstOrDefault(
                t => string.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase));
            if (tag is null)
                return new TagItemsResult(tagName, new List<ItemSummary>(), 0, 0, null, false);

            var boardOrder = _boards.Values
                .OrderBy(b => b.Position).ThenBy(b => b.CreatedAt)
                .Select((b, idx) => (b.Id, idx))
                .ToDictionary(x => x.Id, x => x.idx);

            var all = _items.Values
                .Where(i => !i.IsTemplate)
                .Where(i => includeArchived || !i.Archived)
                .Where(i => i.TagNames.Contains(tag.Name))
                .Where(i => projectId is null
                            || (_boards.TryGetValue(i.BoardId, out var b) && b.ProjectId == projectId))
                .OrderBy(i => boardOrder.GetValueOrDefault(i.BoardId, int.MaxValue))
                .ThenBy(i => i.Position)
                .ThenBy(i => i.CreatedAt)
                .ToList();

            var total = all.Count;
            var lim = limit is > 0 ? Math.Min(limit.Value, 500) : (int?)null;
            var page = lim is null ? all : all.Take(lim.Value).ToList();
            var summaries = BuildSummaries(page, new[] { "tags", "subtasks_count", "comments_count" });
            return new TagItemsResult(tag.Name, summaries, summaries.Count, total, lim,
                lim is not null && summaries.Count < total);
        }
    }

    private List<ItemSummary> BuildSummaries(List<Item> items, IReadOnlyCollection<string>? includes)
    {
        var want = new HashSet<string>(includes ?? Array.Empty<string>());
        var result = new List<ItemSummary>();
        foreach (var i in items)
        {
            var s = new ItemSummary { Item = i };
            if (want.Contains("tags")) s.Tags = ResolveTagsForItem(i);
            if (want.Contains("subtasks_count"))
                s.SubtasksCount = (i.Subtasks.Count, i.Subtasks.Count(x => x.Done));
            if (want.Contains("comments_count"))
                s.CommentsCount = _comments.TryGetValue(i.Id, out var c) ? c.Count : 0;
            result.Add(s);
        }
        return result;
    }

    public Item CreateItem(ItemCreate input, string actor)
    {
        var title = Require(input.Title, "title");
        if (input.Priority is not null && !Priorities.IsValid(input.Priority))
            throw new BadRequestException("priority must be one of low/normal/high");
        if (input.DueDate is not null && !DueDateRe.IsMatch(input.DueDate))
            throw new BadRequestException("due_date must be YYYY-MM-DD");

        Item item;
        lock (_gate)
        {
            if (!_boards.TryGetValue(input.BoardId, out var board)) throw new NotFoundException("Board not found");
            var position = input.Position is null
                ? NextItemPosition(input.BoardId)
                : PlaceAt(input.BoardId, input.Position.Value, exclude: null);

            var number = ++_lastNumber;
            item = new Item
            {
                Id = input.Id ?? Guid.NewGuid(),
                Number = number,
                BoardId = input.BoardId,
                Title = title,
                Description = input.Description,
                Priority = input.Priority ?? Priorities.Normal,
                DueDate = input.DueDate,
                Position = position,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                DoneAt = board.IsDone ? DateTimeOffset.UtcNow : null,
                TagNames = NormalizeTagNames(input.Tags),
            };
            _items[item.Id] = item;
            _itemIdByNumber[item.Number] = item.Id;
            _store.SaveItem(item);
            _store.SaveSequence(_lastNumber);
            if (EnsureTagsExist(item.TagNames)) PersistTags();
            Log(actor, "item.create", "item", item.Id, item.BoardId, item.Id,
                new { title = item.Title, tags = item.TagNames });
        }
        Notify(item.BoardId);
        return item;
    }

    public Item UpdateItem(string idOrNumber, JsonElement patch, string actor)
    {
        Item item;
        Guid boardId;
        Dictionary<string, object?> changed = new();
        lock (_gate)
        {
            item = ResolveItem(idOrNumber);
            boardId = item.BoardId;
            var before = Snapshot(item);
            foreach (var prop in patch.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "title": item.Title = Require(prop.Value.GetString(), "title"); break;
                    case "description": item.Description = ReadNullableString(prop.Value); break;
                    case "priority":
                        var pr = prop.Value.GetString();
                        if (!Priorities.IsValid(pr)) throw new BadRequestException("priority must be one of low/normal/high");
                        item.Priority = pr!;
                        break;
                    case "due_date":
                        var due = ReadNullableString(prop.Value);
                        if (due is not null && !DueDateRe.IsMatch(due))
                            throw new BadRequestException("due_date must be YYYY-MM-DD");
                        item.DueDate = due;
                        break;
                    case "board_id":
                        var guid = prop.Value.GetGuid();
                        if (!_boards.ContainsKey(guid)) throw new NotFoundException("Target board not found");
                        item.BoardId = guid;
                        break;
                }
            }
            item.UpdatedAt = DateTimeOffset.UtcNow;
            _store.SaveItem(item);
            changed = Diff(before, item);
            Log(actor, "item.update", "item", item.Id, item.BoardId, item.Id, new { changed });
        }
        Notify(boardId, item.BoardId);
        return item;
    }

    public Item MoveItem(string idOrNumber, ItemMove move, string actor)
    {
        Item item;
        Guid fromBoard;
        lock (_gate)
        {
            item = ResolveItem(idOrNumber);
            if (!_boards.TryGetValue(move.BoardId, out var targetBoard)) throw new NotFoundException("Target board not found");
            fromBoard = item.BoardId;
            var oldPosition = item.Position;

            var newPosition = PlaceAt(move.BoardId, move.Position, exclude: item.Id);
            item.Position = newPosition;

            var wasDone = _boards.TryGetValue(fromBoard, out var fb) && fb.IsDone;
            item.BoardId = move.BoardId;
            if (targetBoard.IsDone && !wasDone) item.DoneAt = DateTimeOffset.UtcNow;
            else if (!targetBoard.IsDone && wasDone) item.DoneAt = null;

            _store.SaveItem(item);

            Log(actor, "item.move", "item", item.Id, item.BoardId, item.Id, new
            {
                from = new { board_id = fromBoard, position = oldPosition },
                to = new { board_id = item.BoardId, position = item.Position },
            });
        }
        Notify(fromBoard, item.BoardId);
        return item;
    }

    public Item ArchiveItem(string idOrNumber, bool archived, string actor)
    {
        Item item;
        lock (_gate)
        {
            item = ResolveItem(idOrNumber);
            item.Archived = archived;
            item.UpdatedAt = DateTimeOffset.UtcNow;
            _store.SaveItem(item);
            Log(actor, "item.archive", "item", item.Id, item.BoardId, item.Id, new { archived });
        }
        Notify(item.BoardId);
        return item;
    }

    public void DeleteItem(string idOrNumber, string actor)
    {
        Guid boardId;
        lock (_gate)
        {
            var item = ResolveItem(idOrNumber);
            boardId = item.BoardId;
            _store.DeleteItemFile(item);
            _items.Remove(item.Id);
            _itemIdByNumber.Remove(item.Number);
            _comments.Remove(item.Id);
            Log(actor, "item.delete", "item", item.Id, boardId, item.Id, new { title = item.Title });
        }
        Notify(boardId);
    }

    // ------------------------------------------------------------------
    // Subtasks
    // ------------------------------------------------------------------

    public Subtask AddSubtask(string itemIdOrNumber, string title, string actor)
    {
        title = Require(title, "title");
        Item item;
        Subtask subtask;
        lock (_gate)
        {
            item = ResolveItem(itemIdOrNumber);
            var pos = item.Subtasks.Count == 0 ? 1000 : item.Subtasks.Max(s => s.Position) + 1000;
            subtask = new Subtask { Title = title, Position = pos };
            item.Subtasks.Add(subtask);
            _store.SaveItem(item);
            Log(actor, "subtask.create", "subtask", subtask.Id, item.BoardId, item.Id, new { title });
        }
        Notify(item.BoardId);
        return subtask;
    }

    public Subtask UpdateSubtask(Guid subtaskId, JsonElement patch, string actor)
    {
        Item? owner = null;
        Subtask? subtask = null;
        lock (_gate)
        {
            foreach (var i in _items.Values)
            {
                var s = i.Subtasks.FirstOrDefault(x => x.Id == subtaskId);
                if (s is not null) { owner = i; subtask = s; break; }
            }
            if (owner is null || subtask is null) throw new NotFoundException("Subtask not found");

            var onlyDone = true;
            foreach (var prop in patch.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "title": subtask.Title = Require(prop.Value.GetString(), "title"); onlyDone = false; break;
                    case "done": subtask.Done = prop.Value.GetBoolean(); break;
                    case "position": subtask.Position = prop.Value.GetDouble(); onlyDone = false; break;
                    default: onlyDone = false; break;
                }
            }
            subtask.UpdatedAt = DateTimeOffset.UtcNow;
            owner.UpdatedAt = DateTimeOffset.UtcNow;
            _store.SaveItem(owner);
            Log(actor, onlyDone ? "subtask.toggle" : "subtask.update", "subtask", subtask.Id,
                owner.BoardId, owner.Id, onlyDone ? new { done = subtask.Done } : new { });
        }
        Notify(owner.BoardId);
        return subtask;
    }

    public void DeleteSubtask(Guid subtaskId, string actor)
    {
        Item? owner = null;
        lock (_gate)
        {
            foreach (var i in _items.Values)
            {
                var s = i.Subtasks.FirstOrDefault(x => x.Id == subtaskId);
                if (s is not null)
                {
                    owner = i;
                    i.Subtasks.Remove(s);
                    _store.SaveItem(i);
                    Log(actor, "subtask.delete", "subtask", subtaskId, i.BoardId, i.Id, new { title = s.Title });
                    break;
                }
            }
            if (owner is null) throw new NotFoundException("Subtask not found");
        }
        Notify(owner.BoardId);
    }

    // ------------------------------------------------------------------
    // Comments
    // ------------------------------------------------------------------

    public Comment AddComment(string itemIdOrNumber, CommentCreate input, string actor)
    {
        var content = Require(input.Content, "content");
        Item item;
        Comment comment;
        lock (_gate)
        {
            item = ResolveItem(itemIdOrNumber);
            comment = new Comment
            {
                ItemId = item.Id,
                Author = string.IsNullOrWhiteSpace(input.Author) ? actor : input.Author!,
                Content = content,
            };
            if (!_comments.TryGetValue(item.Id, out var list))
                _comments[item.Id] = list = new List<Comment>();
            list.Add(comment);
            _store.SaveComment(comment, item.Number);
            Log(actor, "comment.create", "comment", comment.Id, item.BoardId, item.Id,
                new { author = comment.Author });
        }
        Notify(item.BoardId);
        return comment;
    }

    public void DeleteComment(Guid commentId, string actor)
    {
        Item? owner = null;
        Comment? comment = null;
        lock (_gate)
        {
            foreach (var (itemId, list) in _comments)
            {
                var c = list.FirstOrDefault(x => x.Id == commentId);
                if (c is not null)
                {
                    owner = _items.GetValueOrDefault(itemId);
                    comment = c;
                    list.Remove(c);
                    if (owner is not null)
                    {
                        _store.DeleteComment(c, owner.Number);
                        Log(actor, "comment.delete", "comment", commentId, owner.BoardId, owner.Id, new { });
                    }
                    break;
                }
            }
            if (comment is null) throw new NotFoundException("Comment not found");
        }
        if (owner is not null) Notify(owner.BoardId);
    }

    public List<Comment> CommentsForItem(Guid itemId)
    {
        lock (_gate)
            return _comments.TryGetValue(itemId, out var list) ? list.ToList() : new List<Comment>();
    }

    // ------------------------------------------------------------------
    // Tags
    // ------------------------------------------------------------------

    public List<TagInfo> ListTags(bool includeArchived = false)
    {
        lock (_gate)
        {
            var lastAttached = _activity
                .Where(a => a.Action == "tag.attach" && a.TargetId is not null)
                .GroupBy(a => a.TargetId!.Value)
                .ToDictionary(g => g.Key, g => g.Max(x => x.CreatedAt));
            return _tags.Values
                .Where(t => includeArchived || !t.Archived)
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(t => new TagInfo(t, lastAttached.TryGetValue(t.Id, out var at) ? at : null))
                .ToList();
        }
    }

    public Tag GetTag(Guid id)
    {
        lock (_gate)
            return _tags.TryGetValue(id, out var t) ? t : throw new NotFoundException("Tag not found");
    }

    public Tag CreateTag(TagCreate input, string actor)
    {
        var name = Require(input.Name, "name");
        Tag tag;
        lock (_gate)
        {
            var existing = _tags.Values.FirstOrDefault(t => t.Name == name);
            if (existing is not null)
            {
                ApplyTagInput(existing, input);
                PersistTags();
                Log(actor, "tag.update", "tag", existing.Id, null, null, new { name });
                Notify();
                return existing;
            }
            // 色の指定がなければタグ名から自動配色（Next.js 版 API と同じ）
            tag = new Tag { Name = name, Color = TagPalette.Pick(name) };
            ApplyTagInput(tag, input);
            _tags[tag.Id] = tag;
            PersistTags();
            Log(actor, "tag.create", "tag", tag.Id, null, null, new { name });
        }
        Notify();
        return tag;
    }

    public Tag UpdateTag(Guid id, JsonElement patch, string actor)
    {
        Tag tag;
        lock (_gate)
        {
            tag = GetTag(id);
            var oldName = tag.Name;
            foreach (var prop in patch.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "name": tag.Name = Require(prop.Value.GetString(), "name"); break;
                    case "color": tag.Color = prop.Value.GetString() ?? tag.Color; break;
                    case "path": tag.Path = ReadNullableString(prop.Value); break;
                    case "description": tag.Description = ReadNullableString(prop.Value); break;
                    case "archived": tag.Archived = prop.Value.GetBoolean(); break;
                    case "favorite": tag.Favorite = prop.Value.GetBoolean(); break;
                    case "emoji": tag.Emoji = ReadNullableString(prop.Value); break;
                    case "parent_tag_id":
                        tag.ParentTagId = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.GetGuid();
                        break;
                }
            }
            if (!string.Equals(oldName, tag.Name, StringComparison.Ordinal))
            {
                foreach (var item in _items.Values)
                {
                    var idx = item.TagNames.IndexOf(oldName);
                    if (idx < 0) continue;
                    item.TagNames[idx] = tag.Name;
                    _store.SaveItem(item);
                }
            }
            PersistTags();
            Log(actor, tag.Archived ? "tag.archive" : "tag.update", "tag", tag.Id, null, null, new { name = tag.Name });
        }
        Notify();
        return tag;
    }

    public void DeleteTag(Guid id, string actor)
    {
        lock (_gate)
        {
            if (!_tags.TryGetValue(id, out var tag)) throw new NotFoundException("Tag not found");
            _tags.Remove(id);
            foreach (var item in _items.Values)
            {
                if (item.TagNames.Remove(tag.Name))
                    _store.SaveItem(item);
            }
            foreach (var t in _tags.Values.Where(t => t.ParentTagId == id))
                t.ParentTagId = null;
            PersistTags();
            Log(actor, "tag.delete", "tag", id, null, null, new { name = tag.Name });
        }
        Notify();
    }

    public Tag AttachTag(string itemIdOrNumber, Guid? tagId, string? name, string actor)
    {
        Item item;
        Tag tag;
        lock (_gate)
        {
            item = ResolveItem(itemIdOrNumber);
            if (tagId is null && string.IsNullOrWhiteSpace(name))
                throw new BadRequestException("tag_id or name is required");
            tag = tagId is not null
                ? GetTag(tagId.Value)
                : (_tags.Values.FirstOrDefault(t => t.Name == name) ?? new Tag { Name = name! });
            if (tagId is null && !_tags.ContainsKey(tag.Id))
            {
                _tags[tag.Id] = tag;
                PersistTags();
            }
            if (!item.TagNames.Contains(tag.Name)) item.TagNames.Add(tag.Name);
            _store.SaveItem(item);
            Log(actor, "tag.attach", "tag", tag.Id, item.BoardId, item.Id, new { name = tag.Name });
        }
        Notify(item.BoardId);
        return tag;
    }

    public void DetachTag(string itemIdOrNumber, Guid tagId, string actor)
    {
        Item item;
        Tag tag;
        lock (_gate)
        {
            item = ResolveItem(itemIdOrNumber);
            tag = GetTag(tagId);
            item.TagNames.Remove(tag.Name);
            _store.SaveItem(item);
            Log(actor, "tag.detach", "tag", tagId, item.BoardId, item.Id, new { name = tag.Name });
        }
        Notify(item.BoardId);
    }

    // ------------------------------------------------------------------
    // Activity / search / stats
    // ------------------------------------------------------------------

    public List<ActivityLog> ListActivity(Guid? boardId = null, Guid? itemId = null, string? since = null, int limit = 50)
    {
        DateTimeOffset? sinceDt = null;
        if (!string.IsNullOrWhiteSpace(since) && DateTimeOffset.TryParse(since, out var parsed))
            sinceDt = parsed;
        lock (_gate)
            return _activity
                .Where(a => (boardId is null || a.BoardId == boardId)
                            && (itemId is null || a.ItemId == itemId)
                            && (sinceDt is null || a.CreatedAt >= sinceDt))
                .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList();
    }

    public SearchResult Search(string query, Guid? boardId = null, bool includeArchived = false, int limit = 30)
    {
        query = Require(query, "query");
        var q = query.ToLowerInvariant();
        lock (_gate)
        {
            var hits = new List<SearchHit>();
            foreach (var item in _items.Values)
            {
                if (item.IsTemplate) continue;
                if (boardId is not null && item.BoardId != boardId) continue;
                if (!includeArchived && item.Archived) continue;

                var matchedIn = new List<string>();
                var matchedTags = new List<string>();

                if (item.Title.ToLowerInvariant().Contains(q)) matchedIn.Add("title");
                if ((item.Description ?? "").ToLowerInvariant().Contains(q)) matchedIn.Add("description");

                var tags = ResolveTagsForItem(item);
                foreach (var t in tags)
                    if (t.Name.ToLowerInvariant().Contains(q))
                    {
                        if (!matchedIn.Contains("tag")) matchedIn.Add("tag");
                        matchedTags.Add(t.Name);
                    }
                if (_comments.TryGetValue(item.Id, out var cs)
                    && cs.Any(c => c.Content.ToLowerInvariant().Contains(q)))
                    matchedIn.Add("comment");

                if (matchedIn.Count == 0) continue;

                hits.Add(new SearchHit(
                    new ItemSummary
                    {
                        Item = item,
                        Tags = tags,
                        SubtasksCount = (item.Subtasks.Count, item.Subtasks.Count(s => s.Done)),
                        CommentsCount = _comments.TryGetValue(item.Id, out var c2) ? c2.Count : 0,
                    },
                    matchedIn, matchedTags));
            }

            var ranked = hits
                .OrderByDescending(h => Rank(h))
                .ThenByDescending(h => h.Item.Item.UpdatedAt)
                .Take(Math.Clamp(limit, 1, 200))
                .ToList();
            return new SearchResult(query, ranked, ranked.Count);
        }
    }

    private static int Rank(SearchHit h)
    {
        var r = 0;
        if (h.MatchedIn.Contains("title")) r += 100;
        if (h.MatchedIn.Contains("tag")) r += 50;
        if (h.MatchedIn.Contains("description")) r += 10;
        if (h.MatchedIn.Contains("comment")) r += 5;
        return r;
    }

    public BoardStatsResult BoardStats(Guid boardId)
    {
        lock (_gate)
        {
            if (!_boards.ContainsKey(boardId)) throw new NotFoundException("Board not found");
            var items = _items.Values.Where(i => i.BoardId == boardId && !i.IsTemplate).ToList();
            var active = items.Where(i => !i.Archived).ToList();
            var today = DateOnly.FromDateTime(DateTime.Now);
            var overdue = active.Count(i => i.DueDate is not null
                && DateOnly.TryParse(i.DueDate, out var d) && d < today);
            var dueToday = active.Count(i => i.DueDate is not null
                && DateOnly.TryParse(i.DueDate, out var d) && d == today);
            return new BoardStatsResult(
                boardId,
                items.Count,
                active.Count,
                items.Count - active.Count,
                new Dictionary<string, int>
                {
                    [Priorities.Low] = items.Count(i => i.Priority == Priorities.Low),
                    [Priorities.Normal] = items.Count(i => i.Priority == Priorities.Normal),
                    [Priorities.High] = items.Count(i => i.Priority == Priorities.High),
                },
                overdue,
                dueToday);
        }
    }

    // ------------------------------------------------------------------
    // Position helpers (sparse doubles; renumber on collision)
    // ------------------------------------------------------------------

    private List<Item> ActiveItemsForBoard(Guid boardId)
        => _items.Values
            .Where(i => i.BoardId == boardId && !i.Archived && !i.IsTemplate)
            .OrderBy(i => i.Position).ThenBy(i => i.CreatedAt)
            .ToList();

    private double NextItemPosition(Guid boardId)
    {
        var ordered = ActiveItemsForBoard(boardId);
        return ordered.Count == 0 ? 1000 : ordered[^1].Position + 1000;
    }

    private double PlaceAt(Guid boardId, int targetIndex, Guid? exclude)
    {
        var ordered = ActiveItemsForBoard(boardId)
            .Where(i => exclude is null || i.Id != exclude)
            .ToList();
        var idx = Math.Clamp(targetIndex, 0, ordered.Count);
        // 先頭は「今の先頭 - 1000」に置く（0 との中間を取り続けると隙間がすぐ尽きて、ボード全件の書き直しになる）
        if (idx == 0 && ordered.Count > 0) return ordered[0].Position - 1000;
        var prev = idx > 0 ? ordered[idx - 1].Position : 0;
        var next = idx < ordered.Count ? ordered[idx].Position : prev + 2000;
        var pos = (prev + next) / 2;
        if (next - prev < 1e-6)
        {
            RenumberBoard(boardId, ordered);
            prev = idx > 0 ? ordered[idx - 1].Position : 0;
            next = idx < ordered.Count ? ordered[idx].Position : prev + 2000;
            pos = (prev + next) / 2;
        }
        return pos;
    }

    private void RenumberBoard(Guid boardId, List<Item>? ordered = null)
    {
        ordered ??= ActiveItemsForBoard(boardId);
        var pos = 1000.0;
        foreach (var i in ordered)
        {
            if (Math.Abs(i.Position - pos) > 1e-9)
            {
                i.Position = pos;
                _store.SaveItem(i);
            }
            pos += 1000;
        }
    }

    private int NextProjectPosition()
        => _projects.Values.Where(p => !p.Archived).Select(p => p.Position).DefaultIfEmpty(-1).Max() + 1;

    private int NextBoardPosition(Guid projectId)
        => _boards.Values.Where(b => b.ProjectId == projectId && !b.Archived).Select(b => b.Position).DefaultIfEmpty(-1).Max() + 1;

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private Item ResolveItem(string idOrNumber)
    {
        var key = idOrNumber.Trim();
        if (key.StartsWith('#')) key = key[1..];
        if (Guid.TryParse(key, out var guid) && _items.TryGetValue(guid, out var byId)) return byId;
        if (long.TryParse(key, out var number) && _itemIdByNumber.TryGetValue(number, out var id)
            && _items.TryGetValue(id, out var byNum)) return byNum;
        throw new NotFoundException("Item not found");
    }

    private List<Tag> ResolveTagsForItem(Item item)
    {
        var result = new List<Tag>();
        foreach (var n in item.TagNames)
            if (_tagsByName.TryGetValue(n, out var t)) result.Add(t);
        return result;
    }

    private void ApplyTagInput(Tag tag, TagCreate input)
    {
        if (!string.IsNullOrWhiteSpace(input.Color)) tag.Color = input.Color!;
        if (input.Path is not null) tag.Path = input.Path;
        if (input.Description is not null) tag.Description = input.Description;
        if (input.ParentTagId is not null) tag.ParentTagId = input.ParentTagId;
        if (input.Emoji is not null) tag.Emoji = input.Emoji;
        if (input.Favorite is not null) tag.Favorite = input.Favorite.Value;
    }

    private static List<string> NormalizeTagNames(IReadOnlyList<string>? tags)
        => (tags ?? Array.Empty<string>())
            .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();

    private static string Require(string? value, string field)
        => string.IsNullOrWhiteSpace(value) ? throw new BadRequestException($"{field} is required") : value;

    private static string? ReadNullableString(JsonElement e)
        => e.ValueKind == JsonValueKind.Null ? null : e.GetString();

    private static Dictionary<string, object?> Snapshot(Item i) => new()
    {
        ["title"] = i.Title,
        ["description"] = i.Description,
        ["priority"] = i.Priority,
        ["due_date"] = i.DueDate,
        ["board_id"] = i.BoardId,
        ["archived"] = i.Archived,
    };

    private static Dictionary<string, object?> Diff(Dictionary<string, object?> before, Item after)
    {
        var changed = new Dictionary<string, object?>();
        void Check(string key, object? newValue)
        {
            if (!Equals(before[key], newValue)) changed[key] = new { from = before[key], to = newValue };
        }
        Check("title", after.Title);
        Check("description", after.Description);
        Check("priority", after.Priority);
        Check("due_date", after.DueDate);
        Check("board_id", after.BoardId);
        return changed;
    }

    private void PersistProjects() => _store.SaveProjects(_projects.Values);
    private void PersistBoards() => _store.SaveBoards(_boards.Values);
    private void PersistTags()
    {
        _store.SaveTags(_tags.Values);
        RebuildTagIndex();
    }

    private void Log(string actor, string action, string targetType, Guid? targetId,
        Guid? boardId, Guid? itemId, object details)
    {
        var log = new ActivityLog
        {
            Id = ++_lastActivityId,
            Actor = string.IsNullOrWhiteSpace(actor) ? "user" : actor,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            BoardId = boardId,
            ItemId = itemId,
            Details = JsonSerializer.SerializeToElement(details, VaultJson.LineOptions),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _activity.Add(log);
        _store.AppendActivity(log);
    }

    private void Notify(params Guid[] boards)
        => Changed?.Invoke(this, new VaultChangedEventArgs(boards.Length == 0 ? null : boards));
}
