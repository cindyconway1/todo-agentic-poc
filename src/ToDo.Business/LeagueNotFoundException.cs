namespace ToDo.Business;

// Also thrown for leagues owned by another user: per AC 11 a cross-owner league
// must be indistinguishable from a nonexistent one (404, never 403).
public sealed class LeagueNotFoundException : Exception
{
    public LeagueNotFoundException(Guid leagueId)
        : base($"League '{leagueId}' was not found.")
    {
        LeagueId = leagueId;
    }

    public Guid LeagueId { get; }
}
