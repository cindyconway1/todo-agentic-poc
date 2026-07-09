using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped: tag-ownership rule (BE-04 unit test list, AC 17) — a set LeagueId must reference a
// league owned by the current user; unowned and nonexistent tags are rejected as a not-found so the
// league's existence never leaks. Uses the EF Core in-memory provider — acceptable here because the
// tests target the data-portal *rule logic*, not relational/persistence semantics (the FK's
// ON DELETE SET NULL behavior belongs in the real-SQL integration tests).
public class TeamEditTagOwnershipTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnedLeagueId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UnownedLeagueId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("teams_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        ctx.Leagues.Add(new League { Id = OwnedLeagueId, OwnerUserId = CurrentUserId, Name = "Mine" });
        ctx.Leagues.Add(new League { Id = UnownedLeagueId, OwnerUserId = OtherUserId, Name = "Theirs" });
        await ctx.SaveChangesAsync();

        return provider;
    }

    private static async Task<TeamEdit> NewTeamAsync(ServiceProvider provider, Guid? leagueId)
    {
        var team = await provider.GetRequiredService<IDataPortal<TeamEdit>>().CreateAsync();
        team.Name = "Test Team";
        team.LeagueId = leagueId;
        return team;
    }

    private static LeagueNotFoundException? UnwrapLeagueNotFound(Exception ex) => ex switch
    {
        LeagueNotFoundException notFound => notFound,
        DataPortalException { BusinessException: LeagueNotFoundException notFound } => notFound,
        _ => null,
    };

    [Fact]
    public async Task Insert_WithOwnedLeagueTag_PersistsTag()
    {
        var provider = await BuildProviderAsync();
        var team = await NewTeamAsync(provider, OwnedLeagueId);

        team = await team.SaveAsync();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.Teams.SingleAsync(t => t.Id == team.Id);
        Assert.Equal(OwnedLeagueId, entity.LeagueId);
        Assert.Equal(CurrentUserId, entity.OwnerUserId);
    }

    [Fact]
    public async Task Insert_WithoutTag_Persists()
    {
        var provider = await BuildProviderAsync();
        var team = await NewTeamAsync(provider, leagueId: null);

        team = await team.SaveAsync();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.Teams.SingleAsync(t => t.Id == team.Id);
        Assert.Null(entity.LeagueId);
    }

    [Fact]
    public async Task Insert_WithUnownedLeagueTag_IsRejectedAsNotFound_AndNothingPersists()
    {
        var provider = await BuildProviderAsync();
        var team = await NewTeamAsync(provider, UnownedLeagueId);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => team.SaveAsync());

        Assert.NotNull(UnwrapLeagueNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.Teams.ToListAsync());
    }

    [Fact]
    public async Task Insert_WithNonexistentLeagueTag_IsRejectedAsNotFound_AndNothingPersists()
    {
        var provider = await BuildProviderAsync();
        var team = await NewTeamAsync(provider, Guid.NewGuid());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => team.SaveAsync());

        Assert.NotNull(UnwrapLeagueNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.Teams.ToListAsync());
    }

    [Fact]
    public async Task Update_WithUnownedLeagueTag_IsRejected_AndTagUnchanged()
    {
        var provider = await BuildProviderAsync();
        var created = await NewTeamAsync(provider, leagueId: null);
        created = await created.SaveAsync();

        var portal = provider.GetRequiredService<IDataPortal<TeamEdit>>();
        var fetched = await portal.FetchAsync(created.Id);
        fetched.LeagueId = UnownedLeagueId;

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => fetched.SaveAsync());

        Assert.NotNull(UnwrapLeagueNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.Teams.AsNoTracking().SingleAsync(t => t.Id == created.Id);
        Assert.Null(entity.LeagueId);
    }

    [Fact]
    public async Task Update_SetThenClearTag_Persists()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TeamEdit>>();
        var created = await NewTeamAsync(provider, OwnedLeagueId);
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        Assert.Equal(OwnedLeagueId, fetched.LeagueId);

        fetched.LeagueId = null;
        _ = await fetched.SaveAsync();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.Teams.AsNoTracking().SingleAsync(t => t.Id == created.Id);
        Assert.Null(entity.LeagueId);
    }
}
