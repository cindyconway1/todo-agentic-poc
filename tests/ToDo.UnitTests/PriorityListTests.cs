using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped (BE-10): the PriorityList read model returns the seeded lookup rows ordered by
// SortOrder — the data behind GET /api/priorities. Rows are re-seeded here out of order to
// prove the fetch orders by SortOrder rather than returning insertion/id order.
public class PriorityListTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("prioritylist_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task PriorityList_ReturnsSeededRowsOrderedBySortOrder()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        // EnsureCreated applies the Priorities HasData seed (1 High, 2 Medium, 3 Low).
        await ctx.Database.EnsureCreatedAsync();

        var list = await provider.GetRequiredService<IDataPortal<PriorityList>>().FetchAsync();

        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { 1, 2, 3 }, list.Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "High", "Medium", "Low" }, list.Select(p => p.Name).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, list.Select(p => p.SortOrder).ToArray());
    }

    // The order comes from SortOrder, not Id: with ids and sort orders deliberately reversed,
    // the list must follow SortOrder.
    [Fact]
    public async Task PriorityList_OrdersBySortOrder_NotById()
    {
        var provider = BuildProvider();
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        // No EnsureCreated: hand-seed rows whose Id order disagrees with SortOrder.
        ctx.Priorities.AddRange(
            new Priority { Id = 1, Name = "Last", SortOrder = 3 },
            new Priority { Id = 2, Name = "Middle", SortOrder = 2 },
            new Priority { Id = 3, Name = "First", SortOrder = 1 });
        await ctx.SaveChangesAsync();

        var list = await provider.GetRequiredService<IDataPortal<PriorityList>>().FetchAsync();

        Assert.Equal(new[] { "First", "Middle", "Last" }, list.Select(p => p.Name).ToArray());
    }
}
