using Kanban.Core.Models;

namespace Kanban.Core;

public sealed record BoardItemsPage(
    List<ItemSummary> Items,
    int Count,
    int Total,
    int? Limit,
    int Offset,
    bool HasMore);

public sealed record BoardItemsResult(
    Board Board,
    List<ItemSummary> Items,
    int Count,
    int Total,
    int? Limit,
    int Offset,
    bool HasMore);

public sealed record ProjectBoardItemsResult(
    List<Board> Boards,
    Dictionary<Guid, BoardItemsPage> ByBoard);

public sealed record TagItemsResult(
    string TagName,
    List<ItemSummary> Items,
    int Count,
    int Total,
    int? Limit,
    bool HasMore);

public sealed record TagInfo(Tag Tag, DateTimeOffset? LastAttachedAt);

public sealed record SearchHit(
    ItemSummary Item,
    List<string> MatchedIn,
    List<string> MatchedTags);

public sealed record SearchResult(string Query, List<SearchHit> Results, int Count);

public sealed record BoardStatsResult(
    Guid BoardId,
    int ItemsTotal,
    int ItemsActive,
    int ItemsArchived,
    Dictionary<string, int> ByPriority,
    int Overdue,
    int DueToday);

public sealed record ItemCreate(
    Guid BoardId,
    string Title,
    string? Description = null,
    string? Priority = null,
    string? DueDate = null,
    int? Position = null,
    IReadOnlyList<string>? Tags = null,
    Guid? Id = null);

public sealed record BoardCreate(
    Guid ProjectId,
    string Name,
    string? Description = null,
    string? Color = null,
    int? Position = null,
    bool? ShowNumbering = null,
    bool? IsDone = null,
    string? Layout = null,
    string? Emoji = null);

public sealed record ProjectCreate(
    string Name,
    string? Description = null,
    string? Color = null,
    int? Position = null);

public sealed record TagCreate(
    string Name,
    string? Color = null,
    string? Path = null,
    string? Description = null,
    Guid? ParentTagId = null,
    string? Emoji = null,
    bool? Favorite = null);

public sealed record ItemMove(Guid BoardId, int Position);

public sealed record CommentCreate(string Content, string? Author = null);

public sealed class VaultChangedEventArgs : EventArgs
{
    /// <summary>Null means "everything changed"; otherwise the affected board ids.</summary>
    public IReadOnlyCollection<Guid>? BoardIds { get; }
    public VaultChangedEventArgs(IReadOnlyCollection<Guid>? boardIds) => BoardIds = boardIds;
}
