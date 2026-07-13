namespace ToDo.Api.Dtos;

public sealed class CreateTodoItemRequest
{
    // Nullable so [ApiController] implicit-required validation doesn't 400 a null title
    // before TodoItemEdit's Required rule can produce the contractual 422.
    public string? Title { get; set; }
    public string? Description { get; set; }
    // Optional FK into the Priorities lookup; null/absent means no priority. An id not in the
    // lookup is a 422 from the business layer (GET /api/priorities lists the valid ids).
    public int? PriorityId { get; set; }
    // A malformed or impossible date (e.g. "2026-02-30") fails DateOnly JSON binding → 400.
    public DateOnly? DueDate { get; set; }
}
