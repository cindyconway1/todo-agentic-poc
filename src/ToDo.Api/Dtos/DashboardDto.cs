namespace ToDo.Api.Dtos;

/// <summary>
/// The dashboard (AC 28): the user's lists grouped Leagues / Teams / People ("People" ==
/// Volunteers), each group's entities ordered by name, items incomplete and pre-sorted.
/// </summary>
public sealed class DashboardDto
{
    public List<GroupDto> Leagues { get; set; } = [];
    public List<GroupDto> Teams { get; set; } = [];
    public List<GroupDto> People { get; set; } = [];
}
