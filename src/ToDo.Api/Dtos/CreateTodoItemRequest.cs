namespace ToDo.Api.Dtos;

public sealed class CreateTodoItemRequest
{
    // Nullable so [ApiController] implicit-required validation doesn't 400 a null title
    // before TodoItemEdit's Required rule can produce the contractual 422.
    public string? Title { get; set; }
    public string? Description { get; set; }
    // Optional: "High" | "Medium" | "Low" (case-sensitive); null/absent means no priority.
    public string? Priority { get; set; }
    // A malformed or impossible date (e.g. "2026-02-30") fails DateOnly JSON binding → 400.
    public DateOnly? DueDate { get; set; }
}
