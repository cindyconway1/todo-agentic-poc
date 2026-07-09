using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped: item ownership + persistence through the *real* local data portal (the MobileList
// lesson: persistence-affecting behavior must survive the MobileFormatter clone, so these tests
// SaveAsync and assert the persisted row). Covers: insert stamps OwnerUserId from the CSLA
// context and rejects a list the user doesn't own as a not-found (→ 404) with nothing persisted;
// DueDate round-trips the portal clone; completed items are hidden from fetch/update/delete
// (AC 25's one-way guard). Uses the EF Core in-memory provider — acceptable because these tests
// target data-portal logic, not relational semantics (constraints/sorting on real SQL live in
// ItemsIntegrationTests).
public class TodoItemEditPersistenceTests
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
            o => o.UseInMemoryDatabase("todoitems_" + Guid.NewGuid().ToString("N")),
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

    private static TodoItemNotFoundException? UnwrapItemNotFound(Exception ex) => ex switch
    {
        TodoItemNotFoundException notFound => notFound,
        DataPortalException { BusinessException: TodoItemNotFoundException notFound } => notFound,
        _ => null,
    };

    private static TodoItemListNotFoundException? UnwrapListNotFound(Exception ex) => ex switch
    {
        TodoItemListNotFoundException notFound => notFound,
        DataPortalException { BusinessException: TodoItemListNotFoundException notFound } => notFound,
        _ => null,
    };

    // Insert stamps OwnerUserId from the CSLA context — never from client input — and every
    // field (including DueDate, which must survive the MobileFormatter clone) lands in the row.
    [Fact]
    public async Task Insert_IntoOwnedList_PersistsAllFields_WithOwnerFromContext()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var item = await portal.CreateAsync(OwnedListId);
        item.Title = "  Buy oranges  ";
        item.Description = "the small ones";
        item.DueDate = new DateOnly(2026, 8, 1);
        Assert.True(item.IsValid);
        item = await item.SaveAsync();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(OwnedListId, entity.ListId);
        Assert.Equal(CurrentUserId, entity.OwnerUserId);
        Assert.Equal("Buy oranges", entity.Title); // trimmed
        Assert.Equal("the small ones", entity.Description);
        Assert.Equal(new DateOnly(2026, 8, 1), entity.DueDate); // survived the portal clone
        Assert.False(entity.IsCompleted);
        Assert.Null(entity.CompletedAt);
    }

    // A list the user doesn't own is rejected as a not-found (→ 404, never 403) and nothing
    // persists — a client-supplied OwnerUserId path does not exist.
    [Fact]
    public async Task Insert_IntoUnownedList_IsRejectedAsNotFound_AndNothingPersists()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var item = await portal.CreateAsync(UnownedListId);
        item.Title = "Sneaky";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => item.SaveAsync());

        Assert.NotNull(UnwrapListNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.TodoItems.ToListAsync());
    }

    [Fact]
    public async Task Insert_IntoNonexistentList_IsRejectedAsNotFound_AndNothingPersists()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var item = await portal.CreateAsync(Guid.NewGuid());
        item.Title = "Orphan";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => item.SaveAsync());

        Assert.NotNull(UnwrapListNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.TodoItems.ToListAsync());
    }

    [Fact]
    public async Task Update_ThroughFetchAndSave_PersistsChangedFields_AndCanClearDueDate()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Before";
        created.DueDate = new DateOnly(2026, 8, 1);
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        Assert.Equal(new DateOnly(2026, 8, 1), fetched.DueDate); // fetch round-trips DueDate
        fetched.Title = "After";
        fetched.Description = "now with details";
        fetched.DueDate = null;
        fetched = await fetched.SaveAsync();
        Assert.Equal("After", fetched.Title);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == created.Id);
        Assert.Equal("After", entity.Title);
        Assert.Equal("now with details", entity.Description);
        Assert.Null(entity.DueDate);
        Assert.False(entity.IsCompleted);
    }

    [Fact]
    public async Task Fetch_OfAnotherUsersItem_IsRejectedAsNotFound()
    {
        var provider = await BuildProviderAsync();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var foreignItemId = Guid.NewGuid();
        ctx.TodoItems.Add(new TodoItem
        {
            Id = foreignItemId,
            ListId = UnownedListId,
            OwnerUserId = OtherUserId,
            Title = "Not yours",
        });
        await ctx.SaveChangesAsync();

        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => portal.FetchAsync(foreignItemId));

        Assert.NotNull(UnwrapItemNotFound(ex));
    }

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Doomed";
        created = await created.SaveAsync();

        await portal.DeleteAsync(created.Id);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.TodoItems.ToListAsync());
    }

    [Fact]
    public async Task Delete_OfAnotherUsersItem_IsRejectedAsNotFound_AndRowSurvives()
    {
        var provider = await BuildProviderAsync();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var foreignItemId = Guid.NewGuid();
        ctx.TodoItems.Add(new TodoItem
        {
            Id = foreignItemId,
            ListId = UnownedListId,
            OwnerUserId = OtherUserId,
            Title = "Not yours",
        });
        await ctx.SaveChangesAsync();

        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => portal.DeleteAsync(foreignItemId));

        Assert.NotNull(UnwrapItemNotFound(ex));
        Assert.NotNull(await ctx.TodoItems.SingleOrDefaultAsync(i => i.Id == foreignItemId));
    }

    // AC 25 (one-way guard): once completed, an item is hidden from fetch, update, and delete —
    // there is no path through TodoItemEdit that could touch (let alone un-complete) it.
    [Fact]
    public async Task CompletedItem_CannotBeFetchedUpdatedOrDeleted()
    {
        var provider = await BuildProviderAsync();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var completedItemId = Guid.NewGuid();
        var completedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        ctx.TodoItems.Add(new TodoItem
        {
            Id = completedItemId,
            ListId = OwnedListId,
            OwnerUserId = CurrentUserId,
            Title = "Done and gone",
            IsCompleted = true,
            CompletedAt = completedAt,
        });
        await ctx.SaveChangesAsync();

        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var fetchEx = await Assert.ThrowsAnyAsync<Exception>(() => portal.FetchAsync(completedItemId));
        Assert.NotNull(UnwrapItemNotFound(fetchEx));

        var deleteEx = await Assert.ThrowsAnyAsync<Exception>(() => portal.DeleteAsync(completedItemId));
        Assert.NotNull(UnwrapItemNotFound(deleteEx));

        // The row is untouched: still present, still completed, CompletedAt unchanged.
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == completedItemId);
        Assert.True(entity.IsCompleted);
        Assert.Equal(completedAt, entity.CompletedAt);
    }
}
