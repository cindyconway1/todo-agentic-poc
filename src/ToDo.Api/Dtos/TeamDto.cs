namespace ToDo.Api.Dtos;

public sealed class TeamDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? LeagueId { get; set; }
}
