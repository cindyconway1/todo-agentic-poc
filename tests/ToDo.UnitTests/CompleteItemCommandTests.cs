using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped: one-way completion (AC 25) through the *real* local data portal — the command's
// persisted effects are what matters, so every test executes via IDataPortal and asserts the
// row. Completing an owned item sets IsCompleted + CompletedAt exactly once; an unowned or
// nonexistent item is a not-found (→ 404); an already-completed item is also a not-found, which
// is precisely the "no reverse path" guard — completion can never be re-stamped or undone.
public class CompleteItemCommandTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnedListId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("completecmd_" + Guid.NewGuid().ToString("N")),
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

    private static async Task<Guid> SeedItemAsync(
        ServiceProvider provider, Guid ownerUserId, bool isCompleted = false, DateTime? completedAt = null)
    {
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var id = Guid.NewGuid();
        ctx.TodoItems.Add(new TodoItem
        {
            Id = id,
            ListId = OwnedListId,
            OwnerUserId = ownerUserId,
            Title = "Item",
            IsCompleted = isCompleted,
            CompletedAt = completedAt,
        });
        await ctx.SaveChangesAsync();
        return id;
    }

    private static TodoItemNotFoundException? UnwrapItemNotFound(Exception ex) => ex switch
    {
        TodoItemNotFoundException notFound => notFound,
        DataPortalException { BusinessException: TodoItemNotFoundException notFound } => notFound,
        _ => null,
    };

    // AC 25: completing an owned item persists IsCompleted=true and stamps CompletedAt.
    [Fact]
    public async Task Execute_OnOwnedIncompleteItem_SetsIsCompletedAndCompletedAt()
    {
        var provider = await BuildProviderAsync();
        var itemId = await SeedItemAsync(provider, CurrentUserId);
        var before = DateTime.UtcNow;

        var command = await provider.GetRequiredService<IDataPortal<CompleteItemCommand>>()
            .ExecuteAsync(itemId);

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
        Assert.True(entity.IsCompleted);
        Assert.NotNull(entity.CompletedAt);
        Assert.InRange(entity.CompletedAt!.Value, before, DateTime.UtcNow);
        Assert.Equal(entity.CompletedAt, command.CompletedAt);
    }

    // AC 11: another user's item is a not-found (never a 403) and its row is untouched.
    [Fact]
    public async Task Execute_OnAnotherUsersItem_IsRejectedAsNotFound_AndNothingChanges()
    {
        var provider = await BuildProviderAsync();
        var itemId = await SeedItemAsync(provider, OtherUserId);
        var portal = provider.GetRequiredService<IDataPortal<CompleteItemCommand>>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => portal.ExecuteAsync(itemId));

        Assert.NotNull(UnwrapItemNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
        Assert.False(entity.IsCompleted);
        Assert.Null(entity.CompletedAt);
    }

    [Fact]
    public async Task Execute_OnNonexistentItem_IsRejectedAsNotFound()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<CompleteItemCommand>>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => portal.ExecuteAsync(Guid.NewGuid()));

        Assert.NotNull(UnwrapItemNotFound(ex));
    }

    // AC 25, the one-way guard: an already-completed item is hidden (not-found), so completion
    // can neither be re-stamped nor reversed — CompletedAt is immutable once set. This is the
    // "rejects un-complete" assertion: no business path exists that writes IsCompleted=false,
    // and even the completing command refuses to touch a completed row again.
    [Fact]
    public async Task Execute_OnAlreadyCompletedItem_IsRejectedAsNotFound_AndCompletedAtIsUnchanged()
    {
        var provider = await BuildProviderAsync();
        var originalCompletedAt = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var itemId = await SeedItemAsync(provider, CurrentUserId, isCompleted: true, completedAt: originalCompletedAt);
        var portal = provider.GetRequiredService<IDataPortal<CompleteItemCommand>>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => portal.ExecuteAsync(itemId));

        Assert.NotNull(UnwrapItemNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
        Assert.True(entity.IsCompleted);
        Assert.Equal(originalCompletedAt, entity.CompletedAt);
    }
}
