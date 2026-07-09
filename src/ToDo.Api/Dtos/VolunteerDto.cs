namespace ToDo.Api.Dtos;

public sealed class VolunteerDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid? LeagueId { get; set; }
    public List<Guid> TeamIds { get; set; } = [];
}
