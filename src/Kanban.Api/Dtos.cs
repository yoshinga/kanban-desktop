using System.Text.Json;
using Kanban.Core;
using Kanban.Core.Models;

namespace Kanban.Api;

/// <summary>Response projections that match the original kanban API/MCP shapes.</summary>
internal static class Dtos
{
    public static Dictionary<string, object?> Project(Project p) => new()
    {
        ["id"] = p.Id,
        ["name"] = p.Name,
        ["description"] = p.Description,
        ["color"] = p.Color,
        ["position"] = p.Position,
        ["archived"] = p.Archived,
        ["created_at"] = p.CreatedAt,
        ["updated_at"] = p.UpdatedAt,
    };

    public static Dictionary<string, object?> Board(Board b) => new()
    {
        ["id"] = b.Id,
        ["project_id"] = b.ProjectId,
        ["name"] = b.Name,
        ["description"] = b.Description,
        ["color"] = b.Color,
        ["emoji"] = b.Emoji,
        ["position"] = b.Position,
        ["archived"] = b.Archived,
        ["show_numbering"] = b.ShowNumbering,
        ["is_done"] = b.IsDone,
        ["layout"] = b.Layout,
        ["created_at"] = b.CreatedAt,
        ["updated_at"] = b.UpdatedAt,
    };

    public static Dictionary<string, object?> Tag(Tag t) => new()
    {
        ["id"] = t.Id,
        ["name"] = t.Name,
        ["color"] = t.Color,
        ["path"] = t.Path,
        ["description"] = t.Description,
        ["archived"] = t.Archived,
        ["parent_tag_id"] = t.ParentTagId,
        ["emoji"] = t.Emoji,
        ["favorite"] = t.Favorite,
        ["created_at"] = t.CreatedAt,
    };

    public static Dictionary<string, object?> Tag(TagInfo info)
    {
        var d = Tag(info.Tag);
        d["last_attached_at"] = info.LastAttachedAt;
        return d;
    }

    public static Dictionary<string, object?> Item(Item i) => new()
    {
        ["id"] = i.Id,
        ["number"] = i.Number,
        ["board_id"] = i.BoardId,
        ["title"] = i.Title,
        ["description"] = i.Description,
        ["priority"] = i.Priority,
        ["due_date"] = i.DueDate,
        ["position"] = i.Position,
        ["archived"] = i.Archived,
        ["is_template"] = i.IsTemplate,
        ["tracker_template_id"] = i.TrackerTemplateId,
        ["done_at"] = i.DoneAt,
        ["created_at"] = i.CreatedAt,
        ["updated_at"] = i.UpdatedAt,
    };

    public static Dictionary<string, object?> ItemDetail(Item i)
    {
        var d = Item(i);
        d["tags"] = i.Tags.Select(Tag).ToList();
        d["subtasks"] = i.Subtasks.Select(Subtask).ToList();
        d["comments"] = i.Comments.Select(Comment).ToList();
        return d;
    }

    public static Dictionary<string, object?> ItemSummary(ItemSummary s, bool includeTags, bool includeSubtasks, bool includeComments)
    {
        var d = Item(s.Item);
        if (includeTags) d["tags"] = s.Tags.Select(Tag).ToList();
        if (includeSubtasks) d["subtasks_count"] = new Dictionary<string, object?> { ["total"] = s.SubtasksCount.Total, ["done"] = s.SubtasksCount.Done };
        if (includeComments) d["comments_count"] = s.CommentsCount;
        return d;
    }

    public static Dictionary<string, object?> Subtask(Subtask s) => new()
    {
        ["id"] = s.Id,
        ["title"] = s.Title,
        ["done"] = s.Done,
        ["position"] = s.Position,
        ["created_at"] = s.CreatedAt,
        ["updated_at"] = s.UpdatedAt,
    };

    public static Dictionary<string, object?> Comment(Comment c) => new()
    {
        ["id"] = c.Id,
        ["item_id"] = c.ItemId,
        ["author"] = c.Author,
        ["content"] = c.Content,
        ["created_at"] = c.CreatedAt,
    };

    public static Dictionary<string, object?> Activity(ActivityLog a) => new()
    {
        ["id"] = a.Id,
        ["actor"] = a.Actor,
        ["action"] = a.Action,
        ["target_type"] = a.TargetType,
        ["target_id"] = a.TargetId,
        ["board_id"] = a.BoardId,
        ["item_id"] = a.ItemId,
        ["details"] = a.Details,
        ["created_at"] = a.CreatedAt,
    };
}
