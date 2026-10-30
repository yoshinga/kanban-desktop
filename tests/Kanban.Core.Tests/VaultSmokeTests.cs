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
    public void VaultLoads()
    {
        var vault = FindVault();
        if (vault is null) return; // not present on this machine; skip

        var svc = new KanbanService(vault);
        Assert.NotEmpty(svc.ListProjects());
        Assert.NotEmpty(svc.ListBoards());
        Assert.NotEmpty(svc.ListTags(includeArchived: true));
    }
}
