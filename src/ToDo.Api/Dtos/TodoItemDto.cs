namespace ToDo.Api.Dtos;

public sealed class TodoItemDto
{
    public Guid Id { get; set; }
    public Guid ListId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    // Date-only (serializes as "yyyy-MM-dd"); null means no due date and sorts last.
    public DateOnly? DueDate { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
}
