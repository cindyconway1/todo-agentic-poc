namespace ToDo.Business;

// Also thrown for volunteers owned by another user: per AC 11 a cross-owner volunteer
// must be indistinguishable from a nonexistent one (404, never 403).
public sealed class VolunteerNotFoundException : Exception
{
    public VolunteerNotFoundException(Guid volunteerId)
        : base($"Volunteer '{volunteerId}' was not found.")
    {
        VolunteerId = volunteerId;
    }

    public Guid VolunteerId { get; }
}
