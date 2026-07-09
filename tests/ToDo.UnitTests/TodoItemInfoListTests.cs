using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped: the per-list read model — incomplete items only (AC 25) sorted ascending by
// DueDate with nulls last and CreateDt as the tiebreak (AC 26, 27), and an unowned or
// nonexistent list rejected as a not-found (→ 404, AC 11). The in-memory provider evaluates the
// same LINQ ordering keys; the SQL translation of the ordering is re-asserted against real SQL
// Server in ItemsIntegrationTests.
public class TodoItemInfoListTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnedListId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UnownedListId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("iteminfolist_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        ctx.TodoLists.Add(new TodoList
        {
            Id = OwnedListId,
            OwnerUserId = CurrentUserId,
            ScopeTypeId = 1,
            ScopeEntityId = Guid.NewGuid(),
        });
        ctx.TodoLists.Add(new TodoList
        {
            Id = UnownedListId,
            OwnerUserId = OtherUserId,
            ScopeTypeId = 1,
            ScopeEntityId = Guid.NewGuid(),
        });
        await ctx.SaveChangesAsync();

        return provider;
    }

    private static TodoItem Item(
        string title, DateOnly? dueDate, DateTime createDt, bool isCompleted = false) => new()
    {
        Id = Guid.NewGuid(),
        ListId = OwnedListId,
        OwnerUserId = CurrentUserId,
        Title = title,
        DueDate = dueDate,
        CreateDt = createDt,
        IsCompleted = isCompleted,
        CompletedAt = isCompleted ? createDt : null,
    };

    // AC 26/27 + AC 25 in one shape: due dates ascending, same-date rows tiebroken by CreateDt
    // ascending, null due dates last (also CreateDt-tiebroken), completed rows absent entirely.
    [Fact]
    public async Task Fetch_ReturnsIncompleteItemsSortedByDueDate_NullsLast_CreateDtTiebreak()
    {
        var provider = await BuildProviderAsync();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var baseDt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Inserted out of order on purpose so the assertion can't pass by insertion order.
        ctx.TodoItems.AddRange(
            Item("no-date-late", null, baseDt.AddMinutes(5)),
            Item("mid", new DateOnly(2026, 8, 15), baseDt.AddMinutes(1)),
            Item("early-second", new DateOnly(2026, 8, 1), baseDt.AddMinutes(4)),
            Item("early-first", new DateOnly(2026, 8, 1), baseDt.AddMinutes(2)),
            Item("no-date-early", null, baseDt.AddMinutes(3)),
            Item("completed-hidden", new DateOnly(2026, 8, 1), baseDt, isCompleted: true));
        await ctx.SaveChangesAsync();

        // The audit stamper overwrites CreateDt on Added rows; restore the intended stamps so
        // the tiebreak is deterministic (a Modified save only touches the LastUpdate* columns).
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

        var list = await provider.GetRequiredService<IDataPortal<TodoItemInfoList>>()
            .FetchAsync(OwnedListId);

        Assert.Equal(
            new[] { "early-first", "early-second", "mid", "no-date-early", "no-date-late" },
            list.Select(i => i.Title).ToArray());
        Assert.All(list, i => Assert.False(i.IsCompleted));
    }

    // AC 11: a list the user doesn't own — or one that doesn't exist — is a not-found, not an
    // empty 200, so list existence never leaks.
    [Fact]
    public async Task Fetch_OfUnownedOrNonexistentList_IsRejectedAsNotFound()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemInfoList>>();

        foreach (var listId in new[] { UnownedListId, Guid.NewGuid() })
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => portal.FetchAsync(listId));
            Assert.NotNull(ex switch
            {
                TodoItemListNotFoundException notFound => notFound,
                DataPortalException { BusinessException: TodoItemListNotFoundException notFound } => notFound,
                _ => null,
            });
        }
    }

    [Fact]
    public async Task Fetch_OfEmptyOwnedList_ReturnsEmpty_Not404()
    {
        var provider = await BuildProviderAsync();

        var list = await provider.GetRequiredService<IDataPortal<TodoItemInfoList>>()
            .FetchAsync(OwnedListId);

        Assert.Empty(list);
    }
}
