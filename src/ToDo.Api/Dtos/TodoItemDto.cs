namespace ToDo.Api.Dtos;

public sealed class TodoItemDto
{
    public Guid Id { get; set; }
    public Guid ListId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    // FK into the Priorities lookup (1 High, 2 Medium, 3 Low); null means no priority and
    // sorts last. PriorityName is the lookup row's name, resolved server-side.
    public int? PriorityId { get; set; }
    public string? PriorityName { get; set; }
    // Date-only (serializes as "yyyy-MM-dd"); null means no due date and sorts last.
    public DateOnly? DueDate { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
}
