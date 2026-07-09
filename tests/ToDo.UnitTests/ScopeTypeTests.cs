using ToDo.Business;

namespace ToDo.UnitTests;

// AC-mapped: ScopeType TypeID behavior (BE-06 unit test list) — the reference type is a closed
// set of singleton instances (League/Team/Volunteer), resolvable by id (the persisted form) and
// by name (the route/DTO form), with unknown values rejected.
public class ScopeTypeTests
{
    [Fact]
    public void All_ContainsExactlyLeagueTeamVolunteer_WithDistinctIdsAndNames()
    {
        Assert.Equal(3, ScopeType.All.Count);
        Assert.Contains(ScopeType.League, ScopeType.All);
        Assert.Contains(ScopeType.Team, ScopeType.All);
        Assert.Contains(ScopeType.Volunteer, ScopeType.All);
        Assert.Equal(3, ScopeType.All.Select(t => t.Id).Distinct().Count());
        Assert.Equal(3, ScopeType.All.Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public void FromId_ReturnsTheSameSingletonInstance()
    {
        Assert.Same(ScopeType.League, ScopeType.FromId(ScopeType.League.Id));
        Assert.Same(ScopeType.Team, ScopeType.FromId(ScopeType.Team.Id));
        Assert.Same(ScopeType.Volunteer, ScopeType.FromId(ScopeType.Volunteer.Id));
    }

    [Fact]
    public void FromId_UnknownId_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopeType.FromId(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ScopeType.FromId(99));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(-1, false)]
    public void IsKnownId_MatchesTheClosedSet(int id, bool expected)
    {
        Assert.Equal(expected, ScopeType.IsKnownId(id));
        Assert.Equal(expected, ScopeType.TryFromId(id) is not null);
    }

    [Theory]
    [InlineData("League")]
    [InlineData("league")]
    [InlineData("LEAGUE")]
    public void TryFromName_IsCaseInsensitive(string name)
    {
        Assert.Same(ScopeType.League, ScopeType.TryFromName(name));
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData(null)]
    public void TryFromName_UnknownOrMissingName_ReturnsNull(string? name)
    {
        Assert.Null(ScopeType.TryFromName(name));
    }

    [Fact]
    public void ToString_ReturnsTheName()
    {
        Assert.Equal("Team", ScopeType.Team.ToString());
    }
}
