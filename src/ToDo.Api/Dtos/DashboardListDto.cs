namespace ToDo.Api.Dtos;

/// <summary>
/// One list on the dashboard. Lists are implicit one-per-entity with no name of their own, so
/// ListName is the owning entity's name. Items are incomplete only, pre-sorted; a list with no
/// incomplete items still appears with an empty Items array (empty-state rendering is FE-04).
/// </summary>
public sealed class DashboardListDto
{
    public Guid ListId { get; set; }
    public string ListName { get; set; } = "";
    public List<TodoItemDto> Items { get; set; } = [];
}
