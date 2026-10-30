using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kanban.Api;
using Kanban.Core;
using Kanban.Core.Models;

namespace Kanban.Core.Tests;

public class ApiTests
{
    [Fact]
    public async Task HealthCreateAndListOverHttp()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        using var host = KanbanApiHost.Start(svc, 0, "secret");
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{host.Port}") };

        // unauthorized without key
        var unauth = await http.GetAsync("/api/boards");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        http.DefaultRequestHeaders.Add("X-API-Key", "secret");
        http.DefaultRequestHeaders.Add("X-Actor", "agent:test");

        var health = await http.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.True(health.GetProperty("ok").GetBoolean());

        var project = svc.CreateProject(new ProjectCreate("P"), "test");
        var board = svc.CreateBoard(new BoardCreate(project.Id, "Inbox"), "test");

        var createResp = await http.PostAsJsonAsync("/api/items",
            new { board_id = board.Id, title = "from-http", tags = new[] { "p-kanban" } });
        createResp.EnsureSuccessStatusCode();
        var created = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        var itemId = created.GetProperty("item").GetProperty("id").GetGuid();

        var list = await http.GetFromJsonAsync<JsonElement>($"/api/boards/{board.Id}/items?includes=tags,subtasks_count");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var first = list.GetProperty("items")[0];
        Assert.Equal("from-http", first.GetProperty("title").GetString());
        Assert.Equal("p-kanban", first.GetProperty("tags")[0].GetProperty("name").GetString());

        var detail = await http.GetFromJsonAsync<JsonElement>($"/api/items/{itemId}");
        Assert.Equal("from-http", detail.GetProperty("item").GetProperty("title").GetString());

        var search = await http.GetFromJsonAsync<JsonElement>("/api/search?q=from-http");
        Assert.Equal(1, search.GetProperty("count").GetInt32());

        var byTag = await http.GetFromJsonAsync<JsonElement>("/api/items/by-tag?tag=p-kanban");
        Assert.Equal(1, byTag.GetProperty("total").GetInt32());
        var tagged = byTag.GetProperty("items")[0];
        Assert.Equal("from-http", tagged.GetProperty("title").GetString());
        Assert.Equal("Inbox", tagged.GetProperty("board_name").GetString());
        Assert.Equal("p-kanban", tagged.GetProperty("tags")[0].GetProperty("name").GetString());

        var activity = await http.GetFromJsonAsync<JsonElement>("/api/activity?limit=50");
        Assert.True(activity.GetProperty("logs").GetArrayLength() > 0);
    }
}
