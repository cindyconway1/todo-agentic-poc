namespace ToDo.Api.Dtos;

/// <summary>One entity (league/team/volunteer) on the dashboard with its lists.</summary>
public sealed class GroupDto
{
    public Guid EntityId { get; set; }
    public string EntityName { get; set; } = "";
    public List<DashboardListDto> Lists { get; set; } = [];
}
