using System.Text.Json;
using Kanban.Core;
using Kanban.Core.Models;

namespace Kanban.Core.Tests;

public sealed class TempVault : IDisposable
{
    public string Root { get; } =
        Path.Combine(Path.GetTempPath(), "kanban-tests", Guid.NewGuid().ToString("N"));
    public TempVault() => Directory.CreateDirectory(Root);
    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

public class KanbanServiceTests
{
    private static JsonElement Patch(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public void CreatesAndReloadsFromDisk()
    {
        using var tmp = new TempVault();
        Guid itemNumber;
        Guid boardId;
        {
            var svc = new KanbanService(tmp.Root);
            var project = svc.CreateProject(new ProjectCreate("Personal"), "test");
            var board = svc.CreateBoard(new BoardCreate(project.Id, "Inbox"), "test");
            boardId = board.Id;
            var item = svc.CreateItem(new ItemCreate(board.Id, "hello", Description: "body",
                Tags: new[] { "p-kanban" }), "test");
            itemNumber = item.Id;
            Assert.Equal(1, item.Number);
        }

        var reloaded = new KanbanService(tmp.Root);
        var list = reloaded.ListItemsForBoard(boardId, new[] { "tags" });
        var only = Assert.Single(list.Items);
        Assert.Equal("hello", only.Item.Title);
        Assert.Equal("body", only.Item.Description);
        Assert.Equal(itemNumber, only.Item.Id);
        Assert.Contains(only.Tags, t => t.Name == "p-kanban");
    }

    [Fact]
    public void BoardEmojiPersistsAndCanBeCleared()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var b = svc.CreateBoard(new BoardCreate(p.Id, "Inbox", Emoji: "📥"), "test");
        Assert.Equal("📥", new KanbanService(tmp.Root).GetBoard(b.Id).Emoji);

        svc.UpdateBoard(b.Id, Patch(new { emoji = "✅", name = "Done" }), "test");
        var reloaded = new KanbanService(tmp.Root).GetBoard(b.Id);
        Assert.Equal("✅", reloaded.Emoji);
        Assert.Equal("Done", reloaded.Name);

        svc.UpdateBoard(b.Id, Patch(new { emoji = "" }), "test");
        Assert.Null(new KanbanService(tmp.Root).GetBoard(b.Id).Emoji);
    }

    [Fact]
    public void NumberingIncrementsAndSequencePersists()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var b = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        var a = svc.CreateItem(new ItemCreate(b.Id, "a"), "test");
        var c = svc.CreateItem(new ItemCreate(b.Id, "c"), "test");

        var reloaded = new KanbanService(tmp.Root);
        var d = reloaded.CreateItem(new ItemCreate(b.Id, "d"), "test");
        Assert.Equal(a.Number + 1, c.Number);
        Assert.Equal(c.Number + 1, d.Number);
    }

    [Fact]
    public void MoveIntoDoneBoardSetsDoneAtAndLeavingClears()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var inbox = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        var done = svc.CreateBoard(new BoardCreate(p.Id, "Done", IsDone: true), "test");
        var item = svc.CreateItem(new ItemCreate(inbox.Id, "task"), "test");
        Assert.Null(item.DoneAt);

        var moved = svc.MoveItem(item.Id.ToString(), new ItemMove(done.Id, 0), "test");
        Assert.NotNull(moved.DoneAt);

        var back = svc.MoveItem(item.Id.ToString(), new ItemMove(inbox.Id, 0), "test");
        Assert.Null(back.DoneAt);
    }

    [Fact]
    public void ReorderUsesSparsePositionsAndKeepsOrder()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var b = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        var a = svc.CreateItem(new ItemCreate(b.Id, "a"), "test");
        var bb = svc.CreateItem(new ItemCreate(b.Id, "b"), "test");
        var cc = svc.CreateItem(new ItemCreate(b.Id, "c"), "test");

        svc.MoveItem(cc.Id.ToString(), new ItemMove(b.Id, 0), "test");

        var titles = svc.ListItemsForBoard(b.Id).Items.Select(i => i.Item.Title).ToArray();
        Assert.Equal(new[] { "c", "a", "b" }, titles);
    }

    [Fact]
    public void MoveToAnotherBoardWritesOnlyTheMovedItem()
    {
        using var tmp = new TempVault();
        var store = new Kanban.Core.Vault.VaultStore(tmp.Root);
        var svc = new KanbanService(store);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var inbox = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        var waiting = svc.CreateBoard(new BoardCreate(p.Id, "Waiting"), "test");
        for (var i = 0; i < 5; i++) svc.CreateItem(new ItemCreate(inbox.Id, $"x{i}"), "test");
        // 画面からの追加と同じく先頭に入れて、並びを 1000 刻みからずらす
        var top = svc.CreateItem(new ItemCreate(inbox.Id, "top", Position: 0), "test");
        var second = svc.ListItemsForBoard(inbox.Id).Items[1].Item;

        var before = store.WriteVersion;
        svc.MoveItem(second.Id.ToString(), new ItemMove(waiting.Id, 0), "test");

        // 移動したアイテム 1 件 + アクティビティ 1 行だけ（移動元ボードを書き直さない）
        Assert.Equal(before + 2, store.WriteVersion);
        var titles = svc.ListItemsForBoard(inbox.Id).Items.Select(i => i.Item.Title).ToArray();
        Assert.Equal(new[] { "top", "x1", "x2", "x3", "x4" }, titles);
    }

    [Fact]
    public void RepeatedMovesToTopNeverRenumberTheBoard()
    {
        using var tmp = new TempVault();
        var store = new Kanban.Core.Vault.VaultStore(tmp.Root);
        var svc = new KanbanService(store);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var inbox = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        var done = svc.CreateBoard(new BoardCreate(p.Id, "Done", IsDone: true), "test");
        for (var i = 0; i < 3; i++) svc.CreateItem(new ItemCreate(done.Id, $"old{i}"), "test");
        var moved = new List<Item>();
        for (var i = 0; i < 60; i++) moved.Add(svc.CreateItem(new ItemCreate(inbox.Id, $"x{i}"), "test"));

        // 0 との中間を取る方式だと 30 回ほどで隙間が尽きて Done 全件を書き直していた
        foreach (var item in moved)
        {
            var before = store.WriteVersion;
            svc.MoveItem(item.Id.ToString(), new ItemMove(done.Id, 0), "test");
            Assert.Equal(before + 2, store.WriteVersion);
        }
        var titles = svc.ListItemsForBoard(done.Id).Items.Select(i => i.Item.Title).ToArray();
        Assert.Equal("x59", titles[0]);
        Assert.Equal("x0", titles[59]);
        Assert.Equal("old0", titles[60]);
    }

    [Fact]
    public void ActivityIsRecordedAndPersisted()
    {
        using var tmp = new TempVault();
        {
            var svc = new KanbanService(tmp.Root);
            var p = svc.CreateProject(new ProjectCreate("P"), "test");
            var b = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
            svc.CreateItem(new ItemCreate(b.Id, "x"), "tester");
        }
        var reloaded = new KanbanService(tmp.Root);
        var logs = reloaded.ListActivity(limit: 100);
        Assert.Contains(logs, l => l.Action == "item.create" && l.Actor == "tester");
        Assert.Contains(logs, l => l.Action == "project.create");
    }

    [Fact]
    public void TagRenamePropagatesToItems()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var b = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        var item = svc.CreateItem(new ItemCreate(b.Id, "x"), "test");
        var tag = svc.AttachTag(item.Id.ToString(), null, "old-name", "test");

        svc.UpdateTag(tag.Id, Patch(new { name = "new-name" }), "test");

        var reloaded = new KanbanService(tmp.Root);
        var detail = reloaded.GetItem(item.Id.ToString());
        Assert.Contains("new-name", detail.TagNames);
        Assert.DoesNotContain("old-name", detail.TagNames);
    }

    [Fact]
    public void SearchMatchesTitleAndTag()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var b = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        svc.CreateItem(new ItemCreate(b.Id, "レビューする", Tags: new[] { "review" }), "test");
        svc.CreateItem(new ItemCreate(b.Id, "unrelated"), "test");

        var byTitle = svc.Search("レビュー");
        Assert.Single(byTitle.Results);
        Assert.Contains("title", byTitle.Results[0].MatchedIn);

        var byTag = svc.Search("review");
        Assert.Single(byTag.Results);
        Assert.Contains("tag", byTag.Results[0].MatchedIn);
    }

    [Fact]
    public void ListItemsByTagSpansBoardsAndFiltersArchived()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var inbox = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        var done = svc.CreateBoard(new BoardCreate(p.Id, "Done", IsDone: true), "test");
        var other = svc.CreateBoard(new BoardCreate(p.Id, "Other"), "test");

        var a = svc.CreateItem(new ItemCreate(inbox.Id, "a", Tags: new[] { "p-personal" }), "test");
        var b = svc.CreateItem(new ItemCreate(done.Id, "b", Tags: new[] { "p-personal" }), "test");
        svc.CreateItem(new ItemCreate(other.Id, "c", Tags: new[] { "p-personal" }), "test");
        svc.CreateItem(new ItemCreate(inbox.Id, "unrelated", Tags: new[] { "p-other" }), "test");
        svc.ArchiveItem(b.Id.ToString(), true, "test");

        var found = svc.ListItemsByTag("p-personal");
        Assert.Equal(new[] { "a", "c" }, found.Items.Select(s => s.Item.Title).ToArray());
        Assert.Equal(2, found.Total);

        // case-insensitive name lookup
        var caseless = svc.ListItemsByTag("P-PERSONAL");
        Assert.Equal(2, caseless.Count);

        var withArchived = svc.ListItemsByTag("p-personal", includeArchived: true);
        Assert.Equal(3, withArchived.Count);

        Assert.Empty(svc.ListItemsByTag("does-not-exist").Items);
    }

    [Fact]
    public void CommentsPersistAsSeparateFilesAndSurviveReload()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var b = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        var item = svc.CreateItem(new ItemCreate(b.Id, "x"), "test");
        var c1 = svc.AddComment(item.Id.ToString(), new CommentCreate("first", "user"), "test");
        svc.AddComment(item.Id.ToString(), new CommentCreate("second", "agent"), "test");

        var commentDir = Path.Combine(tmp.Root, "comments", item.Number.ToString());
        Assert.Equal(2, Directory.GetFiles(commentDir, "*.md").Length);

        var reloaded = new KanbanService(tmp.Root);
        var comments = reloaded.CommentsForItem(item.Id);
        Assert.Equal(2, comments.Count);
        Assert.Equal("first", comments[0].Content);

        reloaded.DeleteComment(c1.Id, "test");
        var reloaded2 = new KanbanService(tmp.Root);
        Assert.Single(reloaded2.CommentsForItem(item.Id));
    }

    [Fact]
    public void BoardStatsCountsPriorityAndDue()
    {
        using var tmp = new TempVault();
        var svc = new KanbanService(tmp.Root);
        var p = svc.CreateProject(new ProjectCreate("P"), "test");
        var b = svc.CreateBoard(new BoardCreate(p.Id, "Inbox"), "test");
        svc.CreateItem(new ItemCreate(b.Id, "high", Priority: "high"), "test");
        svc.CreateItem(new ItemCreate(b.Id, "low", Priority: "low"), "test");
        svc.CreateItem(new ItemCreate(b.Id, "overdue", DueDate: "2000-01-01"), "test");

        var stats = svc.BoardStats(b.Id);
        Assert.Equal(3, stats.ItemsTotal);
        Assert.Equal(1, stats.ByPriority["high"]);
        Assert.Equal(1, stats.Overdue);
    }
}
