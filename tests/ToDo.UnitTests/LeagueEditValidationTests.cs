using Csla;
using Csla.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;

namespace ToDo.UnitTests;

// AC-mapped: league name validation (BE-03 unit test list — name required, 1–100 chars).
// Exercises LeagueEdit's business rules through a CSLA local data portal; no database is touched
// because [Create] only assigns an Id and runs rules.
public class LeagueEditValidationTests
{
    private static async Task<LeagueEdit> NewLeagueAsync(string name)
    {
        var services = new ServiceCollection();
        services.AddCsla();
        var provider = services.BuildServiceProvider();
        var league = await provider.GetRequiredService<IDataPortal<LeagueEdit>>().CreateAsync();
        league.Name = name;
        return league;
    }

    [Fact]
    public async Task Name_WhenEmpty_IsRejected()
    {
        var league = await NewLeagueAsync("");

        Assert.False(league.IsValid);
        Assert.Contains(league.BrokenRulesCollection, r => r.Property == nameof(LeagueEdit.Name));
    }

    [Theory]
    [InlineData(1, true)]    // minimum accepted
    [InlineData(100, true)]  // maximum accepted
    [InlineData(101, false)] // above maximum rejected
    public async Task Name_LengthRule_IsEnforced(int length, bool expectedValid)
    {
        var league = await NewLeagueAsync(new string('a', length));

        Assert.Equal(expectedValid, league.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(league.BrokenRulesCollection, r => r.Property == nameof(LeagueEdit.Name));
        }
    }

    [Fact]
    public void Name_MaxLength_ConstantMatchesSpec()
    {
        // Guards the spec'd column width (nvarchar(100)) against silent drift.
        Assert.Equal(100, LeagueEdit.MaxNameLength);
    }
}
