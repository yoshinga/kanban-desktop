namespace Kanban.Core.Vault;

/// <summary>Resolves all file paths inside a Kanban vault.</summary>
public sealed class VaultPaths
{
    public string Root { get; }

    public VaultPaths(string root)
    {
        Root = Path.GetFullPath(root);
    }

    public string VaultJson => Path.Combine(Root, "vault.json");
    public string ProjectsJson => Path.Combine(Root, "projects.json");
    public string BoardsJson => Path.Combine(Root, "boards.json");
    public string TagsJson => Path.Combine(Root, "tags.json");
    public string SequenceJson => Path.Combine(Root, "sequence.json");

    public string ItemsDir => Path.Combine(Root, "items");
    public string CommentsDir => Path.Combine(Root, "comments");
    public string ActivityDir => Path.Combine(Root, "activity");

    public string ItemFile(long number) => Path.Combine(ItemsDir, $"{number}.md");
    public string CommentsForItemDir(long number) => Path.Combine(CommentsDir, number.ToString());
    public string ActivityFile(DateTimeOffset when)
        => Path.Combine(ActivityDir, when.UtcDateTime.ToString("yyyy-MM") + ".ndjson");

    public void EnsureStructure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(ItemsDir);
        Directory.CreateDirectory(CommentsDir);
        Directory.CreateDirectory(ActivityDir);
    }
}
