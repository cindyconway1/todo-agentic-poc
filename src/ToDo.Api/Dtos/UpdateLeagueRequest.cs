namespace ToDo.Api.Dtos;

public sealed class UpdateLeagueRequest
{
    // Nullable so [ApiController] implicit-required validation doesn't 400 a null name
    // before LeagueEdit's Required rule can produce the contractual 422.
    public string? Name { get; set; }
}
