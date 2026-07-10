using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped, DB-free: the BE-08 read models fetched through the real local data portal (which
// exercises the whole root→child fetch chain) against the in-memory provider — dashboard
// grouping by scope type → entity → lists → sorted incomplete items with the stated decisions
// (AC 28), and the flat All-Items view with source labels (AC 29), both excluding completed
// items (AC 25) and other owners' data. The in-memory provider evaluates the same LINQ
// grouping/ordering keys; the SQL translation is re-asserted against real SQL Server in
// ReadModelsIntegrationTests.
public class ReadModelInfoTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("readmodels_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        return services.BuildServiceProvider();
    }

    private static TodoList List(Guid id, ScopeType scopeType, Guid entityId, Guid? ownerId = null) => new()
    {
        Id = id,
        OwnerUserId = ownerId ?? CurrentUserId,
        ScopeTypeId = scopeType.Id,
        ScopeEntityId = entityId,
    };

    private static TodoItem Item(
        Guid listId, string title, DateOnly? dueDate = null, bool isCompleted = false, Guid? ownerId = null) => new()
    {
        Id = Guid.NewGuid(),
        ListId = listId,
        OwnerUserId = ownerId ?? CurrentUserId,
        Title = title,
        DueDate = dueDate,
        IsCompleted = isCompleted,
        CompletedAt = isCompleted ? DateTime.UtcNow : null,
    };

    // AC 28 + the stated decisions: lists land under the correct group and entity ("People" ==
    // Volunteers); entities within a group are ordered by name; an entity with zero lists does
    // not appear; a list with zero incomplete items still appears with empty items; and listName
    // is the owning entity's name.
    [Fact]
    public async Task DashboardFetch_GroupsListsByScopeTypeAndEntity_WithStatedDecisions()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        var league = new League { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Spring League" };
        var leagueNoList = new League { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "No List League" };
        var teamAlpha = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Alpha Team" };
        var teamBravo = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Bravo Team" };
        var volunteer = new Volunteer { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Casey" };
        ctx.AddRange(league, leagueNoList, teamAlpha, teamBravo, volunteer);

        var leagueList = List(Guid.NewGuid(), ScopeType.League, league.Id);
        // Inserted Bravo before Alpha so the name ordering can't pass by insertion order.
        var bravoList = List(Guid.NewGuid(), ScopeType.Team, teamBravo.Id);
        var alphaList = List(Guid.NewGuid(), ScopeType.Team, teamAlpha.Id);
        var volunteerList = List(Guid.NewGuid(), ScopeType.Volunteer, volunteer.Id);
        ctx.AddRange(leagueList, bravoList, alphaList, volunteerList);

        ctx.AddRange(
            Item(leagueList.Id, "league-item"),
            Item(bravoList.Id, "bravo-item"),
            Item(volunteerList.Id, "casey-item"));
        await ctx.SaveChangesAsync();

        var dashboard = await provider.GetRequiredService<IDataPortal<DashboardInfo>>().FetchAsync();

        var leagueGroup = Assert.Single(dashboard.Leagues);
        Assert.Equal(league.Id, leagueGroup.EntityId);
        Assert.Equal("Spring League", leagueGroup.EntityName);
        var leagueListInfo = Assert.Single(leagueGroup.Lists);
        Assert.Equal(leagueList.Id, leagueListInfo.ListId);
        Assert.Equal("Spring League", leagueListInfo.ListName);
        Assert.Equal(new[] { "league-item" }, leagueListInfo.Items.Select(i => i.Title).ToArray());

        Assert.Equal(new[] { "Alpha Team", "Bravo Team" }, dashboard.Teams.Select(g => g.EntityName).ToArray());
        var alphaListInfo = Assert.Single(dashboard.Teams[0].Lists);
        Assert.Equal(alphaList.Id, alphaListInfo.ListId);
        Assert.Empty(alphaListInfo.Items);

        var personGroup = Assert.Single(dashboard.People);
        Assert.Equal("Casey", personGroup.EntityName);
        Assert.Equal(volunteerList.Id, Assert.Single(personGroup.Lists).ListId);
    }

    // AC 26/27 within a dashboard list: due dates ascending, nulls last, CreateDt tiebreak —
    // plus AC 25: a completed item never appears.
    [Fact]
    public async Task DashboardFetch_SortsItemsWithinList_DueDateAscending_NullsLast_CreateDtTiebreak()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        var team = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Sort Team" };
        var list = List(Guid.NewGuid(), ScopeType.Team, team.Id);
        ctx.AddRange(team, list);
        ctx.AddRange(
            Item(list.Id, "no-date-late"),
            Item(list.Id, "mid", new DateOnly(2026, 8, 15)),
            Item(list.Id, "early-second", new DateOnly(2026, 8, 1)),
            Item(list.Id, "early-first", new DateOnly(2026, 8, 1)),
            Item(list.Id, "no-date-early"),
            Item(list.Id, "completed-hidden", new DateOnly(2026, 8, 1), isCompleted: true));
        await ctx.SaveChangesAsync();

        // The audit stamper overwrites CreateDt on Added rows; restore intended stamps so the
        // tiebreak is deterministic (a Modified save only touches the LastUpdate* columns).
        var baseDt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var intended = new Dictionary<string, DateTime>
        {
            ["no-date-late"] = baseDt.AddMinutes(5),
            ["mid"] = baseDt.AddMinutes(1),
            ["early-second"] = baseDt.AddMinutes(4),
            ["early-first"] = baseDt.AddMinutes(2),
            ["no-date-early"] = baseDt.AddMinutes(3),
        };
        foreach (var row in await ctx.TodoItems.Where(i => !i.IsCompleted).ToListAsync())
        {
            row.CreateDt = intended[row.Title];
        }
        await ctx.SaveChangesAsync();

        var dashboard = await provider.GetRequiredService<IDataPortal<DashboardInfo>>().FetchAsync();

        var items = Assert.Single(Assert.Single(dashboard.Teams).Lists).Items;
        Assert.Equal(
            new[] { "early-first", "early-second", "mid", "no-date-early", "no-date-late" },
            items.Select(i => i.Title).ToArray());
    }

    // Ownership: another user's entities, lists, and items never appear on the dashboard.
    [Fact]
    public async Task DashboardFetch_ExcludesOtherOwnersData()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        var mine = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "My Team" };
        var myList = List(Guid.NewGuid(), ScopeType.Team, mine.Id);
        var theirs = new Team { Id = Guid.NewGuid(), OwnerUserId = OtherUserId, Name = "Their Team" };
        var theirList = List(Guid.NewGuid(), ScopeType.Team, theirs.Id, OtherUserId);
        ctx.AddRange(mine, myList, theirs, theirList);
        ctx.AddRange(
            Item(myList.Id, "my-item"),
            Item(theirList.Id, "their-item", ownerId: OtherUserId));
        await ctx.SaveChangesAsync();

        var dashboard = await provider.GetRequiredService<IDataPortal<DashboardInfo>>().FetchAsync();

        var group = Assert.Single(dashboard.Teams);
        Assert.Equal("My Team", group.EntityName);
        Assert.Equal(new[] { "my-item" }, Assert.Single(group.Lists).Items.Select(i => i.Title).ToArray());
        Assert.Empty(dashboard.Leagues);
        Assert.Empty(dashboard.People);
    }

    // AC 29: items flatten across lists and scope types into one sorted list, each row labeled
    // with its source list and entity; completed items (AC 25) and other owners' rows excluded.
    [Fact]
    public async Task AllItemsFetch_FlattensSortedAcrossScopeTypes_WithSourceLabels()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        var league = new League { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "League One" };
        var team = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Team One" };
        var volunteer = new Volunteer { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Vol One" };
        var leagueList = List(Guid.NewGuid(), ScopeType.League, league.Id);
        var teamList = List(Guid.NewGuid(), ScopeType.Team, team.Id);
        var volunteerList = List(Guid.NewGuid(), ScopeType.Volunteer, volunteer.Id);
        ctx.AddRange(league, team, volunteer, leagueList, teamList, volunteerList);
        ctx.AddRange(
            Item(leagueList.Id, "league-item", new DateOnly(2026, 8, 2)),
            Item(teamList.Id, "team-item", new DateOnly(2026, 8, 1)),
            Item(volunteerList.Id, "vol-item"),
            Item(teamList.Id, "completed-hidden", new DateOnly(2026, 8, 1), isCompleted: true));

        var theirTeam = new Team { Id = Guid.NewGuid(), OwnerUserId = OtherUserId, Name = "Their Team" };
        var theirList = List(Guid.NewGuid(), ScopeType.Team, theirTeam.Id, OtherUserId);
        ctx.AddRange(theirTeam, theirList, Item(theirList.Id, "their-item", ownerId: OtherUserId));
        await ctx.SaveChangesAsync();

        var allItems = await provider.GetRequiredService<IDataPortal<AllItemsList>>().FetchAsync();

        Assert.Equal(new[] { "team-item", "league-item", "vol-item" }, allItems.Select(i => i.Title).ToArray());

        Assert.Equal(teamList.Id, allItems[0].ListId);
        Assert.Equal("Team One", allItems[0].ListName);
        Assert.Equal("Team", allItems[0].ScopeTypeName);
        Assert.Equal("Team One", allItems[0].ScopeName);

        Assert.Equal("League", allItems[1].ScopeTypeName);
        Assert.Equal("League One", allItems[1].ScopeName);

        Assert.Equal("Volunteer", allItems[2].ScopeTypeName);
        Assert.Equal("Vol One", allItems[2].ScopeName);
        Assert.Null(allItems[2].DueDate);
    }

    // Empty account: three empty groups and an empty flat list — well-formed, not an error.
    [Fact]
    public async Task ReadModels_ForEmptyAccount_ReturnEmptyShapes()
    {
        var provider = BuildProvider();

        var dashboard = await provider.GetRequiredService<IDataPortal<DashboardInfo>>().FetchAsync();
        Assert.Empty(dashboard.Leagues);
        Assert.Empty(dashboard.Teams);
        Assert.Empty(dashboard.People);

        var allItems = await provider.GetRequiredService<IDataPortal<AllItemsList>>().FetchAsync();
        Assert.Empty(allItems);
    }
}
