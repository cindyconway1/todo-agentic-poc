namespace ToDo.Api.Dtos;

public sealed class CreateVolunteerRequest
{
    // Nullable so [ApiController] implicit-required validation doesn't 400 a null name
    // before VolunteerEdit's Required rule can produce the contractual 422.
    public string? Name { get; set; }
    public Guid? LeagueId { get; set; }
    public List<Guid>? TeamIds { get; set; }
}
