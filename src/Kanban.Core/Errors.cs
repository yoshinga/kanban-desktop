namespace Kanban.Core;

public class KanbanException : Exception
{
    public int Status { get; }
    public KanbanException(string message, int status = 400) : base(message) => Status = status;
}

public sealed class NotFoundException : KanbanException
{
    public NotFoundException(string message = "Not found") : base(message, 404) { }
}

public sealed class BadRequestException : KanbanException
{
    public BadRequestException(string message) : base(message, 400) { }
}

public sealed class ConflictException : KanbanException
{
    public ConflictException(string message) : base(message, 409) { }
}
