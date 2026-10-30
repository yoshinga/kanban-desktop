using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Kanban.Core;
using Kanban.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kanban.Api;

/// <summary>
/// Minimal REST API hosted in-process by the desktop app. Mirrors the original
/// kanban API so the MCP server (and other tools) can talk to it unchanged.
/// </summary>
public sealed class KanbanApiHost : IDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }
    public string Token { get; }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private KanbanApiHost(WebApplication app, int port, string token)
    {
        _app = app;
        Port = port;
        Token = token;
    }

    public static KanbanApiHost Start(KanbanService service, int port, string token)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var app = builder.Build();
        Map(app, service, token);
        app.Start();
        var url = app.Urls.FirstOrDefault() ?? $"http://127.0.0.1:{port}";
        var actual = new Uri(url).Port;
        return new KanbanApiHost(app, actual, token);
    }

    public void Dispose()
    {
        try { _app.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
        try { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
    }

    // ------------------------------------------------------------------

    private static void Map(WebApplication app, KanbanService svc, string token)
    {
        app.Use(async (ctx, next) =>
        {
            try
            {
                if (!ctx.Request.Path.StartsWithSegments("/api/health") && !Authorized(ctx.Request, token))
                {
                    await Write(ctx, 401, new { error = "Unauthorized" });
                    return;
                }
                await next();
            }
            catch (KanbanException e)
            {
                await Write(ctx, e.Status, new { error = e.Message });
            }
            catch (Exception e)
            {
                await Write(ctx, 500, new { error = e.Message });
            }
        });

        string Actor(HttpContext ctx) => ctx.Request.Headers["X-Actor"].FirstOrDefault() ?? "agent";

        app.MapGet("/api/health", () => Results.Json(new { ok = true }, JsonOpts));

        // ---- projects ----
        app.MapGet("/api/projects", (HttpContext ctx) =>
        {
            var includeArchived = BoolQuery(ctx, "archived");
            var list = svc.ListProjects(includeArchived).Select(Dtos.Project).ToList();
            return Results.Json(new { projects = list }, JsonOpts);
        });

        app.MapPost("/api/projects", async (HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var p = svc.CreateProject(new ProjectCreate(
                Str(b, "name") ?? "",
                Str(b, "description"),
                Str(b, "color"),
                IntOrNull(b, "position")), Actor(ctx));
            return Results.Json(new { project = Dtos.Project(p) }, JsonOpts);
        });

        app.MapGet("/api/projects/{id:guid}", (Guid id) =>
            Results.Json(new { project = Dtos.Project(svc.GetProject(id)) }, JsonOpts));

        app.MapPatch("/api/projects/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var p = svc.UpdateProject(id, b, Actor(ctx));
            return Results.Json(new { project = Dtos.Project(p) }, JsonOpts);
        });

        app.MapDelete("/api/projects/{id:guid}", (Guid id, HttpContext ctx) =>
        {
            svc.DeleteProject(id, Actor(ctx));
            return Results.Json(new { deleted = id }, JsonOpts);
        });

        app.MapGet("/api/projects/{id:guid}/board-items", (Guid id, HttpContext ctx) =>
        {
            var inbox = IntQuery(ctx, "inbox_limit") ?? 200;
            var other = IntQuery(ctx, "limit") ?? 50;
            var result = svc.ListProjectBoardItems(id, inbox, other);
            var byBoard = result.ByBoard.ToDictionary(
                kv => kv.Key.ToString(),
                kv => new
                {
                    items = kv.Value.Items.Select(s => Dtos.ItemSummary(s, true, true, true)).ToList(),
                    count = kv.Value.Count,
                    total = kv.Value.Total,
                    limit = kv.Value.Limit,
                    offset = kv.Value.Offset,
                    has_more = kv.Value.HasMore,
                });
            return Results.Json(new { boards = result.Boards.Select(Dtos.Board).ToList(), by_board = byBoard }, JsonOpts);
        });

        // ---- boards ----
        app.MapGet("/api/boards", (HttpContext ctx) =>
        {
            Guid? projectId = Guid.TryParse(ctx.Request.Query["project_id"], out var pid) ? pid : null;
            var includeArchived = BoolQuery(ctx, "archived");
            var list = svc.ListBoards(projectId, includeArchived).Select(Dtos.Board).ToList();
            return Results.Json(new { boards = list }, JsonOpts);
        });

        app.MapPost("/api/boards", async (HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var board = svc.CreateBoard(new BoardCreate(
                Guid.Parse(Str(b, "project_id") ?? throw new BadRequestException("project_id is required")),
                Str(b, "name") ?? "",
                Str(b, "description"),
                Str(b, "color"),
                IntOrNull(b, "position"),
                BoolOrNull(b, "show_numbering"),
                BoolOrNull(b, "is_done"),
                Str(b, "layout"),
                Str(b, "emoji")), Actor(ctx));
            return Results.Json(new { board = Dtos.Board(board) }, JsonOpts);
        });

        app.MapGet("/api/boards/{id:guid}", (Guid id) =>
            Results.Json(new { board = Dtos.Board(svc.GetBoard(id)) }, JsonOpts));

        app.MapPatch("/api/boards/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var board = svc.UpdateBoard(id, b, Actor(ctx));
            return Results.Json(new { board = Dtos.Board(board) }, JsonOpts);
        });

        app.MapDelete("/api/boards/{id:guid}", (Guid id, HttpContext ctx) =>
        {
            svc.DeleteBoard(id, Actor(ctx));
            return Results.Json(new { deleted = id }, JsonOpts);
        });

        app.MapGet("/api/boards/{id:guid}/items", (Guid id, HttpContext ctx) =>
        {
            var includes = (ctx.Request.Query["includes"].FirstOrDefault()
                            ?? ctx.Request.Query["include"].FirstOrDefault()
                            ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var includeArchived = BoolQuery(ctx, "archived");
            var limit = IntQuery(ctx, "limit");
            var offset = IntQuery(ctx, "offset");
            var result = svc.ListItemsForBoard(id, includes, includeArchived, limit, offset);
            var want = includes.ToHashSet();
            var items = result.Items
                .Select(s => Dtos.ItemSummary(s, want.Contains("tags"), want.Contains("subtasks_count"), want.Contains("comments_count")))
                .ToList();
            return Results.Json(new
            {
                board = Dtos.Board(result.Board),
                items,
                count = result.Count,
                total = result.Total,
                limit = result.Limit,
                offset = result.Offset,
                has_more = result.HasMore,
            }, JsonOpts);
        });

        app.MapGet("/api/boards/{id:guid}/stats", (Guid id) =>
        {
            var s = svc.BoardStats(id);
            return Results.Json(new
            {
                board_id = s.BoardId,
                items_total = s.ItemsTotal,
                items_active = s.ItemsActive,
                items_archived = s.ItemsArchived,
                by_priority = s.ByPriority,
                overdue = s.Overdue,
                due_today = s.DueToday,
            }, JsonOpts);
        });

        // ---- items ----
        app.MapPost("/api/items", async (HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var tags = StringArray(b, "tags");
            var item = svc.CreateItem(new ItemCreate(
                Guid.Parse(Str(b, "board_id") ?? throw new BadRequestException("board_id is required")),
                Str(b, "title") ?? "",
                Str(b, "description"),
                Str(b, "priority"),
                Str(b, "due_date"),
                IntOrNull(b, "position"),
                tags,
                GuidOrNull(b, "id")), Actor(ctx));
            return Results.Json(new { item = Dtos.Item(item) }, JsonOpts);
        });

        app.MapGet("/api/items/by-tag", (HttpContext ctx) =>
        {
            var tag = ctx.Request.Query["tag"].FirstOrDefault() ?? "";
            Guid? projectId = Guid.TryParse(ctx.Request.Query["project_id"], out var pid) ? pid : null;
            var includeArchived = BoolQuery(ctx, "archived");
            var limit = IntQuery(ctx, "limit");
            var result = svc.ListItemsByTag(tag, projectId, includeArchived, limit);
            var boardNames = svc.ListBoards(null, true).ToDictionary(b => b.Id, b => b.Name);
            var items = result.Items.Select(s =>
            {
                var d = Dtos.ItemSummary(s, true, true, true);
                d["board_name"] = boardNames.TryGetValue(s.Item.BoardId, out var n) ? n : null;
                return d;
            }).ToList();
            return Results.Json(new
            {
                tag = result.TagName,
                items,
                count = result.Count,
                total = result.Total,
                limit = result.Limit,
                has_more = result.HasMore,
            }, JsonOpts);
        });

        app.MapGet("/api/items/{id}", (string id) =>
        {
            var item = svc.GetItem(id);
            return Results.Json(new { item = Dtos.ItemDetail(item) }, JsonOpts);
        });

        app.MapPatch("/api/items/{id}", async (string id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var item = svc.UpdateItem(id, b, Actor(ctx));
            return Results.Json(new { item = Dtos.Item(item) }, JsonOpts);
        });

        app.MapDelete("/api/items/{id}", (string id, HttpContext ctx) =>
        {
            svc.DeleteItem(id, Actor(ctx));
            return Results.Json(new { deleted = id }, JsonOpts);
        });

        app.MapPost("/api/items/{id}/move", async (string id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var item = svc.MoveItem(id, new ItemMove(
                Guid.Parse(Str(b, "board_id") ?? throw new BadRequestException("board_id is required")),
                IntOrNull(b, "position") ?? 0), Actor(ctx));
            return Results.Json(new { item = Dtos.Item(item) }, JsonOpts);
        });

        app.MapPost("/api/items/{id}/archive", async (string id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var item = svc.ArchiveItem(id, BoolOrNull(b, "archived") ?? true, Actor(ctx));
            return Results.Json(new { item = Dtos.Item(item) }, JsonOpts);
        });

        app.MapPost("/api/items/{id}/subtasks", async (string id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var st = svc.AddSubtask(id, Str(b, "title") ?? "", Actor(ctx));
            return Results.Json(new { subtask = Dtos.Subtask(st) }, JsonOpts);
        });

        app.MapPatch("/api/subtasks/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var st = svc.UpdateSubtask(id, b, Actor(ctx));
            return Results.Json(new { subtask = Dtos.Subtask(st) }, JsonOpts);
        });

        app.MapDelete("/api/subtasks/{id:guid}", (Guid id, HttpContext ctx) =>
        {
            svc.DeleteSubtask(id, Actor(ctx));
            return Results.Json(new { deleted = id }, JsonOpts);
        });

        app.MapPost("/api/items/{id}/comments", async (string id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var c = svc.AddComment(id, new CommentCreate(Str(b, "content") ?? "", Str(b, "author")), Actor(ctx));
            return Results.Json(new { comment = Dtos.Comment(c) }, JsonOpts);
        });

        app.MapDelete("/api/comments/{id:guid}", (Guid id, HttpContext ctx) =>
        {
            svc.DeleteComment(id, Actor(ctx));
            return Results.Json(new { deleted = id }, JsonOpts);
        });

        // ---- tags ----
        app.MapGet("/api/tags", (HttpContext ctx) =>
        {
            var includeArchived = BoolQuery(ctx, "archived");
            var tags = svc.ListTags(includeArchived).Select(Dtos.Tag).ToList();
            return Results.Json(new { tags }, JsonOpts);
        });

        app.MapPost("/api/tags", async (HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var tag = svc.CreateTag(new TagCreate(
                Str(b, "name") ?? "",
                Str(b, "color"),
                Str(b, "path"),
                Str(b, "description"),
                GuidOrNull(b, "parent_tag_id"),
                Str(b, "emoji"),
                BoolOrNull(b, "favorite")), Actor(ctx));
            return Results.Json(new { tag = Dtos.Tag(tag) }, JsonOpts);
        });

        app.MapPatch("/api/tags/{id:guid}", async (Guid id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var tag = svc.UpdateTag(id, b, Actor(ctx));
            return Results.Json(new { tag = Dtos.Tag(tag) }, JsonOpts);
        });

        app.MapDelete("/api/tags/{id:guid}", (Guid id, HttpContext ctx) =>
        {
            svc.DeleteTag(id, Actor(ctx));
            return Results.Json(new { deleted = id }, JsonOpts);
        });

        app.MapPost("/api/items/{id}/tags", async (string id, HttpContext ctx) =>
        {
            var b = await ReadBody(ctx.Request);
            var tag = svc.AttachTag(id, GuidOrNull(b, "tag_id"), Str(b, "name"), Actor(ctx));
            return Results.Json(new { tag = Dtos.Tag(tag) }, JsonOpts);
        });

        app.MapDelete("/api/items/{id}/tags/{tagId:guid}", (string id, Guid tagId, HttpContext ctx) =>
        {
            svc.DetachTag(id, tagId, Actor(ctx));
            return Results.Json(new { detached = new { item_id = id, tag_id = tagId } }, JsonOpts);
        });

        // ---- activity / search ----
        app.MapGet("/api/activity", (HttpContext ctx) =>
        {
            Guid? boardId = Guid.TryParse(ctx.Request.Query["board_id"], out var bid) ? bid : null;
            Guid? itemId = Guid.TryParse(ctx.Request.Query["item_id"], out var iid) ? iid : null;
            var since = ctx.Request.Query["since"].FirstOrDefault();
            var limit = IntQuery(ctx, "limit") ?? 50;
            var logs = svc.ListActivity(boardId, itemId, since, limit).Select(Dtos.Activity).ToList();
            return Results.Json(new { logs }, JsonOpts);
        });

        app.MapGet("/api/search", (HttpContext ctx) =>
        {
            var q = ctx.Request.Query["q"].FirstOrDefault() ?? "";
            Guid? boardId = Guid.TryParse(ctx.Request.Query["board_id"], out var bid) ? bid : null;
            var includeArchived = BoolQuery(ctx, "archived");
            var limit = IntQuery(ctx, "limit") ?? 30;
            var result = svc.Search(q, boardId, includeArchived, limit);
            var results = result.Results.Select(h => new
            {
                item = Dtos.ItemSummary(h.Item, true, true, true),
                matched_in = h.MatchedIn,
                matched_tags = h.MatchedTags,
            }).ToList();
            return Results.Json(new { query = result.Query, results, count = result.Count }, JsonOpts);
        });
    }

    // ------------------------------------------------------------------

    private static bool Authorized(HttpRequest req, string token)
    {
        if (string.IsNullOrEmpty(token)) return true;
        var provided = req.Headers["X-API-Key"].FirstOrDefault()
                       ?? req.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "");
        return string.Equals(provided, token, StringComparison.Ordinal);
    }

    private static async Task Write(HttpContext ctx, int status, object body)
    {
        if (ctx.Response.HasStarted) return;
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(body, JsonOpts));
    }

    private static async Task<JsonElement> ReadBody(HttpRequest req)
    {
        if (req.ContentLength is 0 or null && !req.Body.CanRead) return default;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v.GetString() : null;

    private static int? IntOrNull(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    private static bool? BoolOrNull(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static Guid? GuidOrNull(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var g) ? g : null;

    private static List<string>? StringArray(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return null;
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!).ToList();
    }

    private static bool BoolQuery(HttpContext ctx, string name)
        => string.Equals(ctx.Request.Query[name].FirstOrDefault(), "true", StringComparison.OrdinalIgnoreCase);

    private static int? IntQuery(HttpContext ctx, string name)
        => int.TryParse(ctx.Request.Query[name].FirstOrDefault(), out var v) ? v : null;
}
