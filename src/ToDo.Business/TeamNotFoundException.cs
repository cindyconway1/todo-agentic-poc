namespace ToDo.Business;

// Also thrown for teams owned by another user: per AC 11 a cross-owner team
// must be indistinguishable from a nonexistent one (404, never 403).
public sealed class TeamNotFoundException : Exception
{
    public TeamNotFoundException(Guid teamId)
        : base($"Team '{teamId}' was not found.")
    {
        TeamId = teamId;
    }

    public Guid TeamId { get; }
}
