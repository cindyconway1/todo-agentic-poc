namespace ToDo.Api.Dtos;

public sealed class UpdateTeamRequest
{
    // Nullable so [ApiController] implicit-required validation doesn't 400 a null name
    // before TeamEdit's Required rule can produce the contractual 422.
    public string? Name { get; set; }
    public Guid? LeagueId { get; set; }
}
