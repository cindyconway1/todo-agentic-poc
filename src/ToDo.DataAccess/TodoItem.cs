namespace ToDo.DataAccess;

/// <summary>
/// A to-do item on a list. Completion is one-way: IsCompleted flips to true exactly once
/// (stamping CompletedAt) and no read path ever surfaces a completed item again.
/// OwnerUserId is denormalized from the owning list for the All-Items query (BE-08).
/// </summary>
public class TodoItem : AuditableEntity
{
    public Guid Id { get; set; }
    public Guid ListId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    // Optional, case-sensitive contract values "High" | "Medium" | "Low"; null is valid and
    // sorts last (§7 priority-first sort). Deliberately unvalidated — all four states are legal.
    public string? Priority { get; set; }
    public DateOnly? DueDate { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
}
