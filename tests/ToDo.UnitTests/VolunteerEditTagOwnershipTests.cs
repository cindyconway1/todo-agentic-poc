using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped: tag-ownership rules (BE-05 unit test list, AC 17) — a set LeagueId and every tagged
// TeamId must reference entities owned by the current user; unowned and nonexistent tags are
// rejected as a not-found so their existence never leaks, and no tags are applied. Uses the EF Core
// in-memory provider — acceptable here because the tests target the data-portal *rule logic*, not
// relational/persistence semantics (FK cascade behavior belongs in the real-SQL integration tests).
public class VolunteerEditTagOwnershipTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnedLeagueId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UnownedLeagueId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OwnedTeamId1 = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid OwnedTeamId2 = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid UnownedTeamId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("volunteers_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        ctx.Leagues.Add(new League { Id = OwnedLeagueId, OwnerUserId = CurrentUserId, Name = "Mine" });
        ctx.Leagues.Add(new League { Id = UnownedLeagueId, OwnerUserId = OtherUserId, Name = "Theirs" });
        ctx.Teams.Add(new Team { Id = OwnedTeamId1, OwnerUserId = CurrentUserId, Name = "My Team 1" });
        ctx.Teams.Add(new Team { Id = OwnedTeamId2, OwnerUserId = CurrentUserId, Name = "My Team 2" });
        ctx.Teams.Add(new Team { Id = UnownedTeamId, OwnerUserId = OtherUserId, Name = "Their Team" });
        await ctx.SaveChangesAsync();

        return provider;
    }

    private static async Task<VolunteerEdit> NewVolunteerAsync(
        ServiceProvider provider, Guid? leagueId = null, params Guid[] teamIds)
    {
        var volunteer = await provider.GetRequiredService<IDataPortal<VolunteerEdit>>().CreateAsync();
        volunteer.Name = "Test Volunteer";
        volunteer.LeagueId = leagueId;
        volunteer.TeamIds = teamIds;
        return volunteer;
    }

    private static LeagueNotFoundException? UnwrapLeagueNotFound(Exception ex) => ex switch
    {
        LeagueNotFoundException notFound => notFound,
        DataPortalException { BusinessException: LeagueNotFoundException notFound } => notFound,
        _ => null,
    };

    private static TeamNotFoundException? UnwrapTeamNotFound(Exception ex) => ex switch
    {
        TeamNotFoundException notFound => notFound,
        DataPortalException { BusinessException: TeamNotFoundException notFound } => notFound,
        _ => null,
    };

    [Fact]
    public async Task Insert_WithOwnedLeagueAndTeams_PersistsAllTags()
    {
        var provider = await BuildProviderAsync();
        var volunteer = await NewVolunteerAsync(provider, OwnedLeagueId, OwnedTeamId1, OwnedTeamId2);

        volunteer = await volunteer.SaveAsync();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.Volunteers.SingleAsync(v => v.Id == volunteer.Id);
        Assert.Equal(OwnedLeagueId, entity.LeagueId);
        Assert.Equal(CurrentUserId, entity.OwnerUserId);
        var joinTeamIds = await ctx.VolunteerTeams
            .Where(vt => vt.VolunteerId == volunteer.Id)
            .Select(vt => vt.TeamId)
            .ToListAsync();
        Assert.Equal(new[] { OwnedTeamId1, OwnedTeamId2 }.Order(), joinTeamIds.Order());
    }

    [Fact]
    public async Task Insert_WithUnownedTeamTag_IsRejectedAsNotFound_AndNothingPersists()
    {
        var provider = await BuildProviderAsync();
        var volunteer = await NewVolunteerAsync(provider, null, OwnedTeamId1, UnownedTeamId);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => volunteer.SaveAsync());

        Assert.NotNull(UnwrapTeamNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.Volunteers.ToListAsync());
        Assert.Empty(await ctx.VolunteerTeams.ToListAsync());
    }

    [Fact]
    public async Task Insert_WithNonexistentTeamTag_IsRejectedAsNotFound_AndNothingPersists()
    {
        var provider = await BuildProviderAsync();
        var volunteer = await NewVolunteerAsync(provider, null, Guid.NewGuid());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => volunteer.SaveAsync());

        Assert.NotNull(UnwrapTeamNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.Volunteers.ToListAsync());
        Assert.Empty(await ctx.VolunteerTeams.ToListAsync());
    }

    [Fact]
    public async Task Insert_WithUnownedLeagueTag_IsRejectedAsNotFound_AndNothingPersists()
    {
        var provider = await BuildProviderAsync();
        var volunteer = await NewVolunteerAsync(provider, UnownedLeagueId);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => volunteer.SaveAsync());

        Assert.NotNull(UnwrapLeagueNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.Volunteers.ToListAsync());
    }

    [Fact]
    public async Task Insert_WithNonexistentLeagueTag_IsRejectedAsNotFound_AndNothingPersists()
    {
        var provider = await BuildProviderAsync();
        var volunteer = await NewVolunteerAsync(provider, Guid.NewGuid());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => volunteer.SaveAsync());

        Assert.NotNull(UnwrapLeagueNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.Volunteers.ToListAsync());
    }

    [Fact]
    public async Task Update_WithUnownedTeamTag_IsRejected_AndTagsUnchanged()
    {
        var provider = await BuildProviderAsync();
        var created = await NewVolunteerAsync(provider, null, OwnedTeamId1);
        created = await created.SaveAsync();

        var portal = provider.GetRequiredService<IDataPortal<VolunteerEdit>>();
        var fetched = await portal.FetchAsync(created.Id);
        fetched.TeamIds = new[] { OwnedTeamId1, UnownedTeamId };

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fetched.SaveAsync());

        Assert.NotNull(UnwrapTeamNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var joinTeamIds = await ctx.VolunteerTeams.AsNoTracking()
            .Where(vt => vt.VolunteerId == created.Id)
            .Select(vt => vt.TeamId)
            .ToListAsync();
        Assert.Equal([OwnedTeamId1], joinTeamIds);
    }

    [Fact]
    public async Task Update_ReconcilesTeamTagSet_AddsMissingAndRemovesAbsent()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<VolunteerEdit>>();
        var created = await NewVolunteerAsync(provider, null, OwnedTeamId1);
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        Assert.Equal([OwnedTeamId1], fetched.TeamIds);

        // Swap team 1 for team 2: the reconcile must remove one join row and add the other.
        fetched.TeamIds = new[] { OwnedTeamId2 };
        _ = await fetched.SaveAsync();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var joinTeamIds = await ctx.VolunteerTeams.AsNoTracking()
            .Where(vt => vt.VolunteerId == created.Id)
            .Select(vt => vt.TeamId)
            .ToListAsync();
        Assert.Equal([OwnedTeamId2], joinTeamIds);
    }

    [Fact]
    public async Task Update_SetThenClearLeagueTag_Persists()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<VolunteerEdit>>();
        var created = await NewVolunteerAsync(provider, OwnedLeagueId);
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        Assert.Equal(OwnedLeagueId, fetched.LeagueId);

        fetched.LeagueId = null;
        _ = await fetched.SaveAsync();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.Volunteers.AsNoTracking().SingleAsync(v => v.Id == created.Id);
        Assert.Null(entity.LeagueId);
    }
}
