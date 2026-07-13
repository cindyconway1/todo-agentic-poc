using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped (BE-09): Priority round-trips the *real* local data portal (the MobileList lesson —
// persistence-affecting behavior must survive the MobileFormatter clone, so every test SaveAsyncs
// and asserts both the persisted row and a re-fetched object). Covers: each of 'High', 'Medium',
// 'Low' persists; null persists as null (no silent default); priority changes and clears on
// update; and priority survives an update that touches other fields. Uses the EF Core in-memory
// provider — acceptable because these tests target data-portal logic, not relational semantics
// (the SQL column and sort live in ItemsPriorityIntegrationTests).
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

    // Create with the given priority, save, re-fetch through the portal, and assert the value
    // persisted in the row and round-tripped back out (surviving the MobileFormatter clone).
    private static async Task AssertCreatePersistsPriorityAsync(string? priority)
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var item = await portal.CreateAsync(OwnedListId);
        item.Title = "Prioritized";
        item.Priority = priority;
        Assert.True(item.IsValid); // priority is unvalidated — every value here is valid
        item = await item.SaveAsync();
        Assert.Equal(priority, item.Priority); // echoed on the saved clone

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == item.Id);
        Assert.Equal(priority, entity.Priority); // persisted, not defaulted

        var fetched = await portal.FetchAsync(item.Id);
        Assert.Equal(priority, fetched.Priority); // fetch round-trips it
    }

    [Fact]
    public async Task CreateItem_WithHighPriority_StoresAndReturns() =>
        await AssertCreatePersistsPriorityAsync("High");

    [Fact]
    public async Task CreateItem_WithMediumPriority_StoresAndReturns() =>
        await AssertCreatePersistsPriorityAsync("Medium");

    [Fact]
    public async Task CreateItem_WithLowPriority_StoresAndReturns() =>
        await AssertCreatePersistsPriorityAsync("Low");

    // Null is a valid state of its own: it must persist as NULL, not be coerced to a default
    // like 'Medium' (and not to "" — the getter normalizes CSLA's empty-string coercion back).
    [Fact]
    public async Task CreateItem_WithNullPriority_Allowed() =>
        await AssertCreatePersistsPriorityAsync(null);

    [Fact]
    public async Task UpdateItem_ChangePriorityFromHighToLow_Persists()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Reprioritized";
        created.Priority = "High";
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        Assert.Equal("High", fetched.Priority);
        fetched.Priority = "Low";
        fetched = await fetched.SaveAsync();
        Assert.Equal("Low", fetched.Priority);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == created.Id);
        Assert.Equal("Low", entity.Priority);
    }

    [Fact]
    public async Task UpdateItem_ClearPriority_SetsNull()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Deprioritized";
        created.Priority = "High";
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        fetched.Priority = null;
        fetched = await fetched.SaveAsync();
        Assert.Null(fetched.Priority);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == created.Id);
        Assert.Null(entity.Priority);
    }

    // A title-only edit must not disturb the stored priority — the update writes the fetched
    // (unchanged) value back, so 'High' stays 'High'.
    [Fact]
    public async Task UpdateItem_PreservePriorityOnOtherChanges()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoItemEdit>>();

        var created = await portal.CreateAsync(OwnedListId);
        created.Title = "Before";
        created.Priority = "High";
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(created.Id);
        fetched.Title = "After";
        fetched = await fetched.SaveAsync();
        Assert.Equal("High", fetched.Priority);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == created.Id);
        Assert.Equal("After", entity.Title);
        Assert.Equal("High", entity.Priority);
    }
}
