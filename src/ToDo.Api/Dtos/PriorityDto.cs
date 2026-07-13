namespace ToDo.Api.Dtos;

/// <summary>
/// One row of the Priorities lookup (BE-10): a seeded reference value the frontend uses to
/// populate the priority dropdown. Returned by GET /api/priorities ordered by sortOrder.
/// </summary>
public sealed class PriorityDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
}
