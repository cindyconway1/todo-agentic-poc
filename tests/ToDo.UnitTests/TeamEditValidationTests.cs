using Csla;
using Csla.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;

namespace ToDo.UnitTests;

// AC-mapped: team name validation (BE-04 unit test list — name required, 1–100 chars).
// Exercises TeamEdit's business rules through a CSLA local data portal; no database is touched
// because [Create] only assigns an Id and runs rules.
public class TeamEditValidationTests
{
    private static async Task<TeamEdit> NewTeamAsync(string name)
    {
        var services = new ServiceCollection();
        services.AddCsla();
        var provider = services.BuildServiceProvider();
        var team = await provider.GetRequiredService<IDataPortal<TeamEdit>>().CreateAsync();
        team.Name = name;
        return team;
    }

    [Fact]
    public async Task Name_WhenEmpty_IsRejected()
    {
        var team = await NewTeamAsync("");

        Assert.False(team.IsValid);
        Assert.Contains(team.BrokenRulesCollection, r => r.Property == nameof(TeamEdit.Name));
    }

    [Theory]
    [InlineData(1, true)]    // minimum accepted
    [InlineData(100, true)]  // maximum accepted
    [InlineData(101, false)] // above maximum rejected
    public async Task Name_LengthRule_IsEnforced(int length, bool expectedValid)
    {
        var team = await NewTeamAsync(new string('a', length));

        Assert.Equal(expectedValid, team.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(team.BrokenRulesCollection, r => r.Property == nameof(TeamEdit.Name));
        }
    }

    [Fact]
    public void Name_MaxLength_ConstantMatchesSpec()
    {
        // Guards the spec'd column width (nvarchar(100)) against silent drift.
        Assert.Equal(100, TeamEdit.MaxNameLength);
    }

    [Fact]
    public async Task LeagueId_IsOptional_ValidWithoutTag()
    {
        var team = await NewTeamAsync("Tagless Team");

        Assert.Null(team.LeagueId);
        Assert.True(team.IsValid);
    }
}
