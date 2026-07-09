using Csla;
using Csla.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;

namespace ToDo.UnitTests;

// AC-mapped: volunteer name validation (BE-05 unit test list — name required, 1–100 chars).
// Exercises VolunteerEdit's business rules through a CSLA local data portal; no database is touched
// because [Create] only assigns an Id and runs rules.
public class VolunteerEditValidationTests
{
    private static async Task<VolunteerEdit> NewVolunteerAsync(string name)
    {
        var services = new ServiceCollection();
        services.AddCsla();
        var provider = services.BuildServiceProvider();
        var volunteer = await provider.GetRequiredService<IDataPortal<VolunteerEdit>>().CreateAsync();
        volunteer.Name = name;
        return volunteer;
    }

    [Fact]
    public async Task Name_WhenEmpty_IsRejected()
    {
        var volunteer = await NewVolunteerAsync("");

        Assert.False(volunteer.IsValid);
        Assert.Contains(volunteer.BrokenRulesCollection, r => r.Property == nameof(VolunteerEdit.Name));
    }

    [Theory]
    [InlineData(1, true)]    // minimum accepted
    [InlineData(100, true)]  // maximum accepted
    [InlineData(101, false)] // above maximum rejected
    public async Task Name_LengthRule_IsEnforced(int length, bool expectedValid)
    {
        var volunteer = await NewVolunteerAsync(new string('a', length));

        Assert.Equal(expectedValid, volunteer.IsValid);
        if (!expectedValid)
        {
            Assert.Contains(volunteer.BrokenRulesCollection, r => r.Property == nameof(VolunteerEdit.Name));
        }
    }

    [Fact]
    public void Name_MaxLength_ConstantMatchesSpec()
    {
        // Guards the spec'd column width (nvarchar(100)) against silent drift.
        Assert.Equal(100, VolunteerEdit.MaxNameLength);
    }

    [Fact]
    public async Task Tags_AreOptional_ValidWithoutAny()
    {
        var volunteer = await NewVolunteerAsync("Tagless Volunteer");

        Assert.Null(volunteer.LeagueId);
        Assert.Empty(volunteer.TeamIds);
        Assert.True(volunteer.IsValid);
    }

    [Fact]
    public async Task TeamIds_DuplicatesInInput_AreCollapsedToASet()
    {
        var volunteer = await NewVolunteerAsync("Deduped Volunteer");
        var teamId = Guid.NewGuid();

        volunteer.TeamIds = new[] { teamId, teamId };

        Assert.Equal([teamId], volunteer.TeamIds);
    }
}
