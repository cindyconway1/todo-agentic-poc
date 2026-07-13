using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped (BE-10): PriorityId round-trips the *real* local data portal (the MobileList lesson —
// persistence-affecting behavior must survive the MobileFormatter clone, so every test SaveAsyncs
// and asserts both the persisted row and a re-fetched object). Covers: each seeded id (1 High,
// 2 Medium, 3 Low) persists and resolves its PriorityName from the lookup; null persists as null
// (no silent default); priority changes and clears on update; and priority survives an update
// that touches other fields. Uses the EF Core in-memory provider (EnsureCreated applies the
// HasData seed) — acceptable because these tests target data-portal logic, not relational
// semantics (the FK and SQL sort live in ItemsPriorityIntegrationTests).
public class TodoItemPriorityPersistenceTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OwnedListId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("itempriority_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        // EnsureCreated applies the Priorities HasData seed to the in-memory store.
        await ctx.Database.EnsureCreatedAsync();
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

    // Create with the given priority id, save, re-fetch through the portal, and assert the value
    // persisted in the row and round-tripped back out (surviving the MobileFormatter clone),
    // with PriorityName resolved from the lookup.
    private static async Task AssertCreatePersistsPriorityAsync(int? priorityId, string? expectedName)
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var item = await portal.CreateAsync(OwnedListId);
        item.Title = "Prioritized";
        item.PriorityId = priorityId;
        Assert.True(item.IsValid); // null and every seeded id are valid states
        item = await item.SaveAsync();
        Assert.Equal(priorityId, item.PriorityId); // echoed on the saved clone
        Assert.Equal(expectedName, item.PriorityName); // name resolved from the lookup on save

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(priorityId, entity.PriorityId); // persisted, not defaulted

        var fetched = await portal.FetchAsync(item.Id);
        Assert.Equal(priorityId, fetched.PriorityId); // fetch round-trips it
        Assert.Equal(expectedName, fetched.PriorityName); // fetch joins the lookup for the name
    }

    [Fact]
    public async Task CreateItem_WithHighPriorityId_StoresAndReturnsName() =>
        await AssertCreatePersistsPriorityAsync(1, "High");

    [Fact]
    public async Task CreateItem_WithMediumPriorityId_StoresAndReturnsName() =>
        await AssertCreatePersistsPriorityAsync(2, "Medium");

    [Fact]
    public async Task CreateItem_WithLowPriorityId_StoresAndReturnsName() =>
        await AssertCreatePersistsPriorityAsync(3, "Low");

    // Null is a valid state of its own: it must persist as NULL, not be coerced to a default,
    // and PriorityName must be null too.
    [Fact]
    public async Task CreateItem_WithNullPriorityId_Allowed() =>
        await AssertCreatePersistsPriorityAsync(null, null);

    [Fact]
    public async Task UpdateItem_ChangePriorityFromHighToLow_Persists()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Reprioritized";
        created.PriorityId = 1;
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        Assert.Equal(1, fetched.PriorityId);
        Assert.Equal("High", fetched.PriorityName);
        fetched.PriorityId = 3;
        fetched = await fetched.SaveAsync();
        Assert.Equal(3, fetched.PriorityId);
        Assert.Equal("Low", fetched.PriorityName);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == created.Id);
        Assert.Equal(3, entity.PriorityId);
    }

    [Fact]
    public async Task UpdateItem_ClearPriorityId_SetsNull()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Deprioritized";
        created.PriorityId = 1;
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        fetched.PriorityId = null;
        fetched = await fetched.SaveAsync();
        Assert.Null(fetched.PriorityId);
        Assert.Null(fetched.PriorityName);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == created.Id);
        Assert.Null(entity.PriorityId);
    }

    // A title-only edit must not disturb the stored priority — the update writes the fetched
    // (unchanged) value back, so High stays High.
    [Fact]
    public async Task UpdateItem_PreservePriorityIdOnOtherChanges()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Before";
        created.PriorityId = 1;
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        fetched.Title = "After";
        fetched = await fetched.SaveAsync();
        Assert.Equal(1, fetched.PriorityId);
        Assert.Equal("High", fetched.PriorityName);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == created.Id);
        Assert.Equal("After", entity.Title);
        Assert.Equal(1, entity.PriorityId);
    }
}
