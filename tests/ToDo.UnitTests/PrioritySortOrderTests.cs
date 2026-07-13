using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped (BE-09, spec §7 update): the priority-first sort — Priority High → Medium → Low →
// null, then DueDate ascending nulls last, then CreateDt ascending — asserted by index order in
// every read model that returns incomplete items: TodoItemInfoList (per-list), AllItemsList
// (flat all-items), and DashboardInfo (per-list within the grouped dashboard). Items are always
// inserted out of expected order so no assertion can pass by insertion order. The in-memory
// provider evaluates the same LINQ ordering keys; the SQL translation is re-asserted against
// real SQL Server in ItemsPriorityIntegrationTests.
public class PrioritySortOrderTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OwnedListId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("prioritysort_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        return services.BuildServiceProvider();
    }

    private static async Task<ServiceProvider> BuildProviderWithOwnedListAsync()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        ctx.TodoLists.Add(new TodoList
        {
            Id = OwnedListId,
            OwnerUserId = CurrentUserId,
            ScopeTypeId = 1,
            ScopeEntityId = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync();
        return provider;
    }

    private static TodoItem Item(string title, string? priority, DateOnly? dueDate, Guid? listId = null) => new()
    {
        Id = Guid.NewGuid(),
        ListId = listId ?? OwnedListId,
        OwnerUserId = CurrentUserId,
        Title = title,
        Priority = priority,
        DueDate = dueDate,
    };

    // The audit stamper overwrites CreateDt on Added rows; restore the intended per-title stamps
    // so the CreateDt tiebreak is deterministic (a Modified save only touches LastUpdate*).
    private static async Task RestoreCreateDtAsync(
        ApplicationDbContext ctx, IReadOnlyDictionary<string, DateTime> intended)
    {
        foreach (var row in await ctx.TodoItems.ToListAsync())
        {
            row.CreateDt = intended[row.Title];
        }
        await ctx.SaveChangesAsync();
    }

    private static readonly DateTime BaseDt = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // AC "Sort by Priority, then DueDate": one item per priority level, each with a due date
    // *later* than the next level's — priority must dominate the due date, so High/07-25 still
    // comes before null/07-10.
    [Fact]
    public async Task TodoItemInfoList_SortsByPriorityThenDueDate_HighPriorityFirst()
    {
        var provider = await BuildProviderWithOwnedListAsync();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        // Inserted in reverse of the expected output.
        ctx.TodoItems.AddRange(
            Item("none", null, new DateOnly(2026, 7, 10)),
            Item("low", "Low", new DateOnly(2026, 7, 15)),
            Item("medium", "Medium", new DateOnly(2026, 7, 20)),
            Item("high", "High", new DateOnly(2026, 7, 25)));
        await ctx.SaveChangesAsync();

        var list = await provider.GetRequiredService<IDataPortal<TodoItemInfoList>>()
            .FetchAsync(OwnedListId);

        Assert.Equal(new[] { "high", "medium", "low", "none" }, list.Select(i => i.Title).ToArray());
        Assert.Equal(new string?[] { "High", "Medium", "Low", null }, list.Select(i => i.Priority).ToArray());
    }

    // AC "Within each priority level: ascending by DueDate, nulls last": three High items —
    // dates ascending, the date-less one last, regardless of CreateDt order.
    [Fact]
    public async Task TodoItemInfoList_SortsByPriorityThenDueDate_WithinSamePriority()
    {
        var provider = await BuildProviderWithOwnedListAsync();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        ctx.TodoItems.AddRange(
            Item("high-late", "High", new DateOnly(2026, 7, 25)),
            Item("high-early", "High", new DateOnly(2026, 7, 20)),
            Item("high-nodate", "High", null));
        await ctx.SaveChangesAsync();
        await RestoreCreateDtAsync(ctx, new Dictionary<string, DateTime>
        {
            ["high-late"] = BaseDt.AddMinutes(1),   // created first…
            ["high-early"] = BaseDt.AddMinutes(2),
            ["high-nodate"] = BaseDt.AddMinutes(3), // …so CreateDt alone would give the wrong order
        });

        var list = await provider.GetRequiredService<IDataPortal<TodoItemInfoList>>()
            .FetchAsync(OwnedListId);

        Assert.Equal(new[] { "high-early", "high-late", "high-nodate" }, list.Select(i => i.Title).ToArray());
    }

    // AC "Priority order: High → Medium → Low → null (nulls last)": with every DueDate null, the
    // priority rank alone decides the order.
    [Fact]
    public async Task TodoItemInfoList_SortsByPriorityThenDueDate_NullPriorityLast()
    {
        var provider = await BuildProviderWithOwnedListAsync();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        ctx.TodoItems.AddRange(
            Item("none", null, null),
            Item("medium", "Medium", null),
            Item("high", "High", null),
            Item("low", "Low", null));
        await ctx.SaveChangesAsync();
        // Equalize CreateDt so the assertion can only pass via the priority rank.
        await RestoreCreateDtAsync(ctx, new Dictionary<string, DateTime>
        {
            ["none"] = BaseDt,
            ["medium"] = BaseDt,
            ["high"] = BaseDt,
            ["low"] = BaseDt,
        });

        var list = await provider.GetRequiredService<IDataPortal<TodoItemInfoList>>()
            .FetchAsync(OwnedListId);

        Assert.Equal(new[] { "high", "medium", "low", "none" }, list.Select(i => i.Title).ToArray());
    }

    // AC "Tiebreak: ascending by CreateDt": same priority, same due date — only CreateDt decides.
    [Fact]
    public async Task TodoItemInfoList_SortsByCreateDtTiebreak_SamePriorityAndDate()
    {
        var provider = await BuildProviderWithOwnedListAsync();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        var dueDate = new DateOnly(2026, 7, 20);
        // Inserted with the later-created item first.
        ctx.TodoItems.AddRange(
            Item("created-second", "High", dueDate),
            Item("created-first", "High", dueDate));
        await ctx.SaveChangesAsync();
        await RestoreCreateDtAsync(ctx, new Dictionary<string, DateTime>
        {
            ["created-first"] = BaseDt,
            ["created-second"] = BaseDt.AddSeconds(1),
        });

        var list = await provider.GetRequiredService<IDataPortal<TodoItemInfoList>>()
            .FetchAsync(OwnedListId);

        Assert.Equal(new[] { "created-first", "created-second" }, list.Select(i => i.Title).ToArray());
    }

    // AC "AllItemsList applies the priority-first sort": mixed priorities/dates spread across
    // two lists (different scope entities) flatten into one correctly ordered result.
    [Fact]
    public async Task AllItemsList_SortsByPriorityThenDueDate()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        var teamA = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Team A" };
        var teamB = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Team B" };
        var listA = new TodoList { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, ScopeTypeId = ScopeType.Team.Id, ScopeEntityId = teamA.Id };
        var listB = new TodoList { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, ScopeTypeId = ScopeType.Team.Id, ScopeEntityId = teamB.Id };
        ctx.AddRange(teamA, teamB, listA, listB);

        // Interleaved across the two lists, inserted out of expected order: the sort must hold
        // globally across lists, not merely within each list.
        ctx.TodoItems.AddRange(
            Item("none-a", null, new DateOnly(2026, 7, 10), listA.Id),
            Item("low-b", "Low", new DateOnly(2026, 7, 15), listB.Id),
            Item("high-nodate-b", "High", null, listB.Id),
            Item("medium-a", "Medium", new DateOnly(2026, 7, 20), listA.Id),
            Item("high-b", "High", new DateOnly(2026, 7, 25), listB.Id),
            Item("high-a", "High", new DateOnly(2026, 7, 20), listA.Id));
        await ctx.SaveChangesAsync();

        var allItems = await provider.GetRequiredService<IDataPortal<AllItemsList>>().FetchAsync();

        Assert.Equal(
            new[] { "high-a", "high-b", "high-nodate-b", "medium-a", "low-b", "none-a" },
            allItems.Select(i => i.Title).ToArray());
        // Priority round-trips into the flat read model too.
        Assert.Equal("High", allItems[0].Priority);
        Assert.Null(allItems[^1].Priority);
    }

    // AC "DashboardInfo applies the priority-first sort per list": two entities, each list with
    // mixed priorities — every list's items independently follow the §7 order.
    [Fact]
    public async Task DashboardInfo_SortsByPriorityThenDueDate_PerList()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();

        var teamA = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Alpha" };
        var teamB = new Team { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, Name = "Bravo" };
        var listA = new TodoList { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, ScopeTypeId = ScopeType.Team.Id, ScopeEntityId = teamA.Id };
        var listB = new TodoList { Id = Guid.NewGuid(), OwnerUserId = CurrentUserId, ScopeTypeId = ScopeType.Team.Id, ScopeEntityId = teamB.Id };
        ctx.AddRange(teamA, teamB, listA, listB);

        ctx.TodoItems.AddRange(
            Item("a-none", null, new DateOnly(2026, 7, 10), listA.Id),
            Item("a-high", "High", new DateOnly(2026, 7, 25), listA.Id),
            Item("a-medium", "Medium", new DateOnly(2026, 7, 20), listA.Id),
            Item("b-low", "Low", null, listB.Id),
            Item("b-high-nodate", "High", null, listB.Id),
            Item("b-high-dated", "High", new DateOnly(2026, 7, 25), listB.Id));
        await ctx.SaveChangesAsync();

        var dashboard = await provider.GetRequiredService<IDataPortal<DashboardInfo>>().FetchAsync();

        Assert.Equal(new[] { "Alpha", "Bravo" }, dashboard.Teams.Select(g => g.EntityName).ToArray());
        var alphaItems = Assert.Single(dashboard.Teams[0].Lists).Items;
        Assert.Equal(new[] { "a-high", "a-medium", "a-none" }, alphaItems.Select(i => i.Title).ToArray());
        Assert.Equal(new string?[] { "High", "Medium", null }, alphaItems.Select(i => i.Priority).ToArray());

        var bravoItems = Assert.Single(dashboard.Teams[1].Lists).Items;
        Assert.Equal(
            new[] { "b-high-dated", "b-high-nodate", "b-low" },
            bravoItems.Select(i => i.Title).ToArray());
    }
}
