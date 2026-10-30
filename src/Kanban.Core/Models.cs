namespace Kanban.Core.Models;

using System.Text.Json;

public static class Priorities
{
    public const string Low = "low";
    public const string Normal = "normal";
    public const string High = "high";
    public static bool IsValid(string? p) => p is Low or Normal or High;
}

public static class BoardLayouts
{
    public const string Scroll = "scroll";
    public const string Board = "board";
    public static bool IsValid(string? l) => l is Scroll or Board;
}

public sealed class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Color { get; set; } = "#64748b";
    public int Position { get; set; }
    public bool Archived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Board
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Color { get; set; } = "#64748b";
    public string? Emoji { get; set; }
    public int Position { get; set; }
    public bool Archived { get; set; }
    public bool ShowNumbering { get; set; }
    public bool IsDone { get; set; }
    public string Layout { get; set; } = BoardLayouts.Scroll;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Subtask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public double Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Item
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Number { get; set; }
    public Guid BoardId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public double Position { get; set; }
    public string Priority { get; set; } = Priorities.Normal;
    public string? DueDate { get; set; }
    public bool Archived { get; set; }
    public bool IsTemplate { get; set; }
    public Guid? TrackerTemplateId { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<Subtask> Subtasks { get; set; } = new();

    // Tag names as persisted in the item's front matter. Resolved to Tag ids by the service.
    public List<string> TagNames { get; set; } = new();

    // Filled in for detail responses (not persisted here)
    public List<Tag> Tags { get; set; } = new();
    public List<Comment> Comments { get; set; } = new();
}

public sealed class Comment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ItemId { get; set; }
    public string Author { get; set; } = "user";
    public string Content { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#94a3b8";
    public string? Path { get; set; }
    public string? Description { get; set; }
    public bool Archived { get; set; }
    public Guid? ParentTagId { get; set; }
    public string? Emoji { get; set; }
    public bool Favorite { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ActivityLog
{
    public long Id { get; set; }
    public string Actor { get; set; } = "user";
    public string Action { get; set; } = "";
    public string TargetType { get; set; } = "";
    public Guid? TargetId { get; set; }
    public Guid? BoardId { get; set; }
    public Guid? ItemId { get; set; }
    /// <summary>Structured JSON payload (as stored in the source data / ndjson).</summary>
    public JsonElement? Details { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Item with resolved tags and counts, used by list endpoints.</summary>
public sealed class ItemSummary
{
    public Item Item { get; init; } = new();
    public List<Tag> Tags { get; set; } = new();
    public (int Total, int Done) SubtasksCount { get; set; }
    public int CommentsCount { get; set; }
}
