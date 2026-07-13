namespace ToDo.Api.Dtos;

/// <summary>
/// One row of the All-Items view (AC 29): an incomplete item labeled with its source list and
/// scope entity. ListName equals ScopeName because lists are implicit one-per-entity.
/// </summary>
public sealed class AllItemDto
{
    public Guid Id { get; set; }
    public Guid ListId { get; set; }
    public string ListName { get; set; } = "";
    // ScopeType TypeID name: "League", "Team", or "Volunteer".
    public string ScopeType { get; set; } = "";
    public string ScopeName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    // FK into the Priorities lookup (1 High, 2 Medium, 3 Low); null means no priority and
    // sorts last. PriorityName is the lookup row's name, resolved server-side.
    public int? PriorityId { get; set; }
    public string? PriorityName { get; set; }
    // Date-only (serializes as "yyyy-MM-dd"); null means no due date and sorts last.
    public DateOnly? DueDate { get; set; }
}
