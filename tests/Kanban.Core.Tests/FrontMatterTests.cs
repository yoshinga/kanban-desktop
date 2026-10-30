using System.Text.Json;
using Kanban.Core;
using Kanban.Core.Models;
using Kanban.Core.Vault;

namespace Kanban.Core.Tests;

public class FrontMatterTests
{
    [Fact]
    public void RoundTripsScalarsListsAndMaps()
    {
        var entries = new List<KeyValuePair<string, object?>>
        {
            new("id", Guid.Parse("4c7b79bb-3533-4066-b473-850488caf95b")),
            new("number", 1097L),
            new("title", "test: with, special #chars"),
            new("priority", "normal"),
            new("due", (string?)null),
            new("tags", new List<string> { "p-kanban", "idea" }),
            new("archived", false),
            new("position", 1500.0),
            new("subtasks", new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?> { ["id"] = Guid.NewGuid(), ["title"] = "a, b", ["done"] = false, ["position"] = 1000.0 },
            }),
        };
        var text = FrontMatter.Compose(entries, "body line 1\nbody line 2\n");
        var (meta, body) = FrontMatter.Parse(text);

        Assert.Equal("test: with, special #chars", FrontMatter.Str(meta, "title"));
        Assert.Equal(1097, FrontMatter.Long(meta, "number"));
        Assert.Null(FrontMatter.Str(meta, "due"));
        Assert.Equal(new[] { "p-kanban", "idea" }, FrontMatter.StringList(meta, "tags"));
        Assert.Equal(1500.0, FrontMatter.Dbl(meta, "position"));
        Assert.Equal("body line 1\nbody line 2\n", body);

        var maps = FrontMatter.MapList(meta, "subtasks");
        Assert.Single(maps);
        Assert.Equal("a, b", FrontMatter.Str(maps[0], "title"));
    }
}
