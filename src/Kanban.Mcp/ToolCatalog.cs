using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kanban.Mcp;

internal static class ToolCatalog
{
    private sealed record ToolDef(string Name, string Description, string SchemaJson);

    private static readonly ToolDef[] Tools =
    {
        new("list_projects", "List projects (top-level containers of boards).",
            """{"type":"object","properties":{"archived":{"type":"boolean"}}}"""),
        new("create_project", "Create a project.",
            """{"type":"object","properties":{"name":{"type":"string"},"color":{"type":"string"},"description":{"type":["string","null"]}},"required":["name"]}"""),
        new("list_boards", "List boards (columns: Inbox/Doing/Done etc). Pass project_id to filter.",
            """{"type":"object","properties":{"project_id":{"type":"string"},"include_archived":{"type":"boolean"}}}"""),
        new("get_board", "Get a single board by UUID.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        new("create_board", "Create a board under a project.",
            """{"type":"object","properties":{"project_id":{"type":"string"},"name":{"type":"string"},"description":{"type":["string","null"]},"color":{"type":"string"},"emoji":{"type":["string","null"]},"position":{"type":"integer"},"show_numbering":{"type":"boolean"},"is_done":{"type":"boolean"},"layout":{"type":"string","enum":["scroll","board"]}},"required":["project_id","name"]}"""),
        new("update_board", "Update board fields. Only included keys are changed.",
            """{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"description":{"type":["string","null"]},"color":{"type":"string"},"emoji":{"type":["string","null"]},"position":{"type":"integer"},"archived":{"type":"boolean"},"show_numbering":{"type":"boolean"},"is_done":{"type":"boolean"},"layout":{"type":"string","enum":["scroll","board"]}},"required":["id"]}"""),
        new("delete_board", "Delete a board. Its items are deleted too.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        new("board_stats", "Per-board aggregate stats.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        new("list_items", "List items in a board ordered by position. includes=['tags','subtasks_count','comments_count'].",
            """{"type":"object","properties":{"board_id":{"type":"string"},"includes":{"type":"array","items":{"type":"string","enum":["tags","subtasks_count","comments_count"]}},"include_archived":{"type":"boolean"},"limit":{"type":"integer"},"offset":{"type":"integer"}},"required":["board_id"]}"""),
        new("list_items_by_tag", "List every item carrying a tag across all boards in one call (tags, subtasks_count, comments_count and board_name included). Use this to resolve a p-*/w-* category tag without walking each board.",
            """{"type":"object","properties":{"name":{"type":"string"},"project_id":{"type":"string"},"include_archived":{"type":"boolean"},"limit":{"type":"integer"}},"required":["name"]}"""),
        new("get_item", "Get an item with tags, subtasks and comments. Accepts UUID or #number.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        new("create_item", "Create an item. Tags listed by name are created if missing.",
            """{"type":"object","properties":{"board_id":{"type":"string"},"title":{"type":"string"},"description":{"type":["string","null"]},"priority":{"type":"string","enum":["low","normal","high"]},"due_date":{"type":["string","null"]},"position":{"type":"integer"},"tags":{"type":"array","items":{"type":"string"}},"id":{"type":"string"}},"required":["board_id","title"]}"""),
        new("update_item", "Update an item's title/description/priority/due_date.",
            """{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string"},"description":{"type":["string","null"]},"priority":{"type":"string","enum":["low","normal","high"]},"due_date":{"type":["string","null"]}},"required":["id"]}"""),
        new("move_item", "Move an item to a board at the given position (0-based).",
            """{"type":"object","properties":{"id":{"type":"string"},"board_id":{"type":"string"},"position":{"type":"integer"}},"required":["id","board_id","position"]}"""),
        new("delete_item", "Delete an item (subtasks/comments/tag links included).",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        new("archive_item", "Set the archived flag on an item.",
            """{"type":"object","properties":{"id":{"type":"string"},"archived":{"type":"boolean"}},"required":["id","archived"]}"""),
        new("add_subtask", "Append a subtask to an item.",
            """{"type":"object","properties":{"item_id":{"type":"string"},"title":{"type":"string"}},"required":["item_id","title"]}"""),
        new("update_subtask", "Update a subtask (title/done/position).",
            """{"type":"object","properties":{"id":{"type":"string"},"title":{"type":"string"},"done":{"type":"boolean"},"position":{"type":"number"}},"required":["id"]}"""),
        new("delete_subtask", "Delete a subtask.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        new("add_comment", "Add a comment to an item.",
            """{"type":"object","properties":{"item_id":{"type":"string"},"content":{"type":"string"},"author":{"type":"string"}},"required":["item_id","content"]}"""),
        new("delete_comment", "Delete a comment by UUID.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        new("list_tags", "List tags with last_attached_at. Archived hidden unless include_archived.",
            """{"type":"object","properties":{"include_archived":{"type":"boolean"}}}"""),
        new("create_tag", "Create (or upsert by name) a tag.",
            """{"type":"object","properties":{"name":{"type":"string"},"color":{"type":"string"},"path":{"type":["string","null"]},"description":{"type":["string","null"]},"parent_tag_id":{"type":["string","null"]},"emoji":{"type":["string","null"]},"favorite":{"type":"boolean"}},"required":["name"]}"""),
        new("update_tag", "Update a tag's fields.",
            """{"type":"object","properties":{"id":{"type":"string"},"name":{"type":"string"},"color":{"type":"string"},"path":{"type":["string","null"]},"description":{"type":["string","null"]},"archived":{"type":"boolean"},"favorite":{"type":"boolean"},"emoji":{"type":["string","null"]},"parent_tag_id":{"type":["string","null"]}},"required":["id"]}"""),
        new("delete_tag", "Delete a tag and detach it from items.",
            """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""),
        new("attach_tag", "Attach a tag to an item by tag_id or name (auto-creates).",
            """{"type":"object","properties":{"item_id":{"type":"string"},"tag_id":{"type":"string"},"name":{"type":"string"}},"required":["item_id"]}"""),
        new("detach_tag", "Remove a tag from an item.",
            """{"type":"object","properties":{"item_id":{"type":"string"},"tag_id":{"type":"string"}},"required":["item_id","tag_id"]}"""),
        new("list_activity", "List recent activity, newest first.",
            """{"type":"object","properties":{"board_id":{"type":"string"},"item_id":{"type":"string"},"since":{"type":"string"},"limit":{"type":"integer"}}}"""),
        new("search", "Substring search across title/description/tag/comment.",
            """{"type":"object","properties":{"query":{"type":"string"},"board_id":{"type":"string"},"include_archived":{"type":"boolean"},"limit":{"type":"integer"}},"required":["query"]}"""),
    };

    public static JsonArray Build()
    {
        var arr = new JsonArray();
        foreach (var t in Tools)
        {
            JsonNode? schema;
            try { schema = JsonNode.Parse(t.SchemaJson); }
            catch (JsonException) { schema = new JsonObject { ["type"] = "object" }; }
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = schema,
            });
        }
        return arr;
    }

    public static Task<string> InvokeAsync(ApiClient api, string name, JsonObject a) => name switch
    {
        "list_projects" => api.Get("/api/projects" + Q(("archived", B(a, "archived")))),
        "create_project" => api.Post("/api/projects", Body(a)),
        "list_boards" => api.Get("/api/boards" + BoardsQuery(a)),
        "get_board" => api.Get($"/api/boards/{S(a, "id")}"),
        "create_board" => api.Post("/api/boards", Body(a)),
        "update_board" => api.Patch($"/api/boards/{S(a, "id")}", Body(a, "id")),
        "delete_board" => api.Delete($"/api/boards/{S(a, "id")}"),
        "board_stats" => api.Get($"/api/boards/{S(a, "id")}/stats"),
        "list_items" => api.Get($"/api/boards/{S(a, "board_id")}/items" + ListItemsQuery(a)),
        "list_items_by_tag" => api.Get("/api/items/by-tag" + TagItemsQuery(a)),
        "get_item" => api.Get($"/api/items/{Uri.EscapeDataString(S(a, "id") ?? "")}"),
        "create_item" => api.Post("/api/items", Body(a)),
        "update_item" => api.Patch($"/api/items/{Uri.EscapeDataString(S(a, "id") ?? "")}", Body(a, "id")),
        "move_item" => api.Post($"/api/items/{Uri.EscapeDataString(S(a, "id") ?? "")}/move", Body(a, "id")),
        "delete_item" => api.Delete($"/api/items/{Uri.EscapeDataString(S(a, "id") ?? "")}"),
        "archive_item" => api.Post($"/api/items/{Uri.EscapeDataString(S(a, "id") ?? "")}/archive", Body(a, "id")),
        "add_subtask" => api.Post($"/api/items/{Uri.EscapeDataString(S(a, "item_id") ?? "")}/subtasks", Body(a, "item_id")),
        "update_subtask" => api.Patch($"/api/subtasks/{S(a, "id")}", Body(a, "id")),
        "delete_subtask" => api.Delete($"/api/subtasks/{S(a, "id")}"),
        "add_comment" => api.Post($"/api/items/{Uri.EscapeDataString(S(a, "item_id") ?? "")}/comments", Body(a, "item_id")),
        "delete_comment" => api.Delete($"/api/comments/{S(a, "id")}"),
        "list_tags" => api.Get("/api/tags" + Q(("archived", B(a, "include_archived")))),
        "create_tag" => api.Post("/api/tags", Body(a)),
        "update_tag" => api.Patch($"/api/tags/{S(a, "id")}", Body(a, "id")),
        "delete_tag" => api.Delete($"/api/tags/{S(a, "id")}"),
        "attach_tag" => api.Post($"/api/items/{Uri.EscapeDataString(S(a, "item_id") ?? "")}/tags", Body(a, "item_id")),
        "detach_tag" => api.Delete($"/api/items/{Uri.EscapeDataString(S(a, "item_id") ?? "")}/tags/{S(a, "tag_id")}"),
        "list_activity" => api.Get("/api/activity" + ActivityQuery(a)),
        "search" => api.Get("/api/search" + SearchQuery(a)),
        _ => throw new ApiException($"Unknown tool: {name}"),
    };

    private static string BoardsQuery(JsonObject a)
    {
        var parts = new List<string>();
        if (S(a, "project_id") is { } pid) parts.Add("project_id=" + pid);
        if (B(a, "include_archived") is true) parts.Add("archived=true");
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static string ListItemsQuery(JsonObject a)    {
        var parts = new List<string>();
        if (a["includes"] is JsonArray inc)
            parts.Add("includes=" + Uri.EscapeDataString(string.Join(",", inc.Select(x => x?.GetValue<string>()))));
        if (B(a, "include_archived") is true) parts.Add("archived=true");
        if (I(a, "limit") is int l) parts.Add("limit=" + l);
        if (I(a, "offset") is int o) parts.Add("offset=" + o);
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static string ActivityQuery(JsonObject a)
    {
        var parts = new List<string>();
        if (S(a, "board_id") is { } b) parts.Add("board_id=" + b);
        if (S(a, "item_id") is { } i) parts.Add("item_id=" + i);
        if (S(a, "since") is { } s) parts.Add("since=" + Uri.EscapeDataString(s));
        if (I(a, "limit") is int l) parts.Add("limit=" + l);
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static string TagItemsQuery(JsonObject a)
    {
        var parts = new List<string> { "tag=" + Uri.EscapeDataString(S(a, "name") ?? "") };
        if (S(a, "project_id") is { } pid) parts.Add("project_id=" + pid);
        if (B(a, "include_archived") is true) parts.Add("archived=true");
        if (I(a, "limit") is int l) parts.Add("limit=" + l);
        return "?" + string.Join("&", parts);
    }

    private static string SearchQuery(JsonObject a)
    {
        var parts = new List<string> { "q=" + Uri.EscapeDataString(S(a, "query") ?? "") };
        if (S(a, "board_id") is { } b) parts.Add("board_id=" + b);
        if (B(a, "include_archived") is true) parts.Add("archived=true");
        if (I(a, "limit") is int l) parts.Add("limit=" + l);
        return "?" + string.Join("&", parts);
    }

    private static JsonObject Body(JsonObject a, params string[] exclude)
    {
        var o = new JsonObject();
        foreach (var kv in a)
        {
            if (exclude.Contains(kv.Key)) continue;
            o[kv.Key] = kv.Value?.DeepClone();
        }
        return o;
    }

    private static string Q(params (string Key, bool? Value)[] pairs)
    {
        var parts = pairs.Where(p => p.Value is true).Select(p => p.Key + "=true").ToList();
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private static string? S(JsonObject a, string k)
        => a[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? I(JsonObject a, string k)
        => a[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool? B(JsonObject a, string k)
        => a[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}
