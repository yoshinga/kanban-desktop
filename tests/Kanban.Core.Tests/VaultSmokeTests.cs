using Kanban.Core;

namespace Kanban.Core.Tests;

public class VaultSmokeTests
{
    private static string? FindVault()
    {
        var env = Environment.GetEnvironmentVariable("KANBAN_VAULT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        return null; // KANBAN_VAULT 未指定の環境ではスキップ
    }

    [Fact]
    public void ImportedVaultLoadsAndResolves()
    {
        var vault = FindVault();
        if (vault is null) return; // not present on this machine; skip

        var svc = new KanbanService(vault);
        var projects = svc.ListProjects();
        Assert.True(projects.Count >= 2, $"expected >=2 projects, got {projects.Count}");

        var boards = svc.ListBoards();
        Assert.True(boards.Count >= 8, $"expected >=8 boards, got {boards.Count}");

        var tags = svc.ListTags(includeArchived: true);
        Assert.True(tags.Count >= 40, $"expected >=40 tags, got {tags.Count}");

        // Item #1097 is the migration task itself.
        var item = svc.GetItem("1097");
        Assert.Equal(1097, item.Number);
        Assert.Contains("Dropbox", item.Title);

        // The Agent project has an Inbox with items.
        var agent = projects.FirstOrDefault(p => p.Name == "Agent");
        Assert.NotNull(agent);
        var agentBoards = svc.ListBoards(agent!.Id);
        Assert.NotEmpty(agentBoards);
    }
}
