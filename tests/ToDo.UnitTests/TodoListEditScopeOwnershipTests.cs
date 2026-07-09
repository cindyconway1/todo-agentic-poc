using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped: the polymorphic scope-ownership rule (BE-06 unit test list, AC 20) — a list may only
// be created for a scope entity that exists and is owned by the current user; unowned and
// nonexistent scope entities are both rejected as a not-found so their existence never leaks, and
// nothing persists. Also covers the TodoListEdit validation rules (unknown scope type / empty
// scope entity id → invalid). Uses the EF Core in-memory provider — acceptable here because these
// tests target the data-portal rule logic, not relational semantics (the unique scope index /
// idempotency belongs to the real-SQL integration tests).
public class TodoListEditScopeOwnershipTests
{
    private static readonly Guid CurrentUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OwnedLeagueId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OwnedTeamId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid OwnedVolunteerId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid UnownedLeagueId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid UnownedTeamId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly Guid UnownedVolunteerId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");

    private static async Task<ServiceProvider> BuildProviderAsync()
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(CurrentUserId));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("todolists_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        var provider = services.BuildServiceProvider();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        ctx.Leagues.Add(new League { Id = OwnedLeagueId, OwnerUserId = CurrentUserId, Name = "My League" });
        ctx.Teams.Add(new Team { Id = OwnedTeamId, OwnerUserId = CurrentUserId, Name = "My Team" });
        ctx.Volunteers.Add(new Volunteer { Id = OwnedVolunteerId, OwnerUserId = CurrentUserId, Name = "My Volunteer" });
        ctx.Leagues.Add(new League { Id = UnownedLeagueId, OwnerUserId = OtherUserId, Name = "Their League" });
        ctx.Teams.Add(new Team { Id = UnownedTeamId, OwnerUserId = OtherUserId, Name = "Their Team" });
        ctx.Volunteers.Add(new Volunteer { Id = UnownedVolunteerId, OwnerUserId = OtherUserId, Name = "Their Volunteer" });
        await ctx.SaveChangesAsync();

        return provider;
    }

    private static ScopeEntityNotFoundException? UnwrapScopeEntityNotFound(Exception ex) => ex switch
    {
        ScopeEntityNotFoundException notFound => notFound,
        DataPortalException { BusinessException: ScopeEntityNotFoundException notFound } => notFound,
        _ => null,
    };

    public static TheoryData<int, string> OwnedScopes => new()
    {
        { ScopeType.League.Id, nameof(OwnedLeagueId) },
        { ScopeType.Team.Id, nameof(OwnedTeamId) },
        { ScopeType.Volunteer.Id, nameof(OwnedVolunteerId) },
    };

    public static TheoryData<int, string> UnownedScopes => new()
    {
        { ScopeType.League.Id, nameof(UnownedLeagueId) },
        { ScopeType.Team.Id, nameof(UnownedTeamId) },
        { ScopeType.Volunteer.Id, nameof(UnownedVolunteerId) },
    };

    private static Guid ScopeEntity(string fieldName) => fieldName switch
    {
        nameof(OwnedLeagueId) => OwnedLeagueId,
        nameof(OwnedTeamId) => OwnedTeamId,
        nameof(OwnedVolunteerId) => OwnedVolunteerId,
        nameof(UnownedLeagueId) => UnownedLeagueId,
        nameof(UnownedTeamId) => UnownedTeamId,
        nameof(UnownedVolunteerId) => UnownedVolunteerId,
        _ => throw new ArgumentOutOfRangeException(nameof(fieldName)),
    };

    [Theory]
    [MemberData(nameof(OwnedScopes))]
    public async Task Insert_ScopedToOwnedEntity_PersistsWithOwnerFromContext(int scopeTypeId, string scopeField)
    {
        var provider = await BuildProviderAsync();
        var scopeEntityId = ScopeEntity(scopeField);
        var portal = provider.GetRequiredService<IDataPortal<TodoListEdit>>();

        var list = await portal.CreateAsync(scopeTypeId, scopeEntityId);
        Assert.True(list.IsValid);
        list = await list.SaveAsync();

        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        var entity = await ctx.TodoLists.SingleAsync(l => l.Id == list.Id);
        Assert.Equal(scopeTypeId, entity.ScopeTypeId);
        Assert.Equal(scopeEntityId, entity.ScopeEntityId);
        // Owner is derived from the CSLA context, never from client input.
        Assert.Equal(CurrentUserId, entity.OwnerUserId);
    }

    [Theory]
    [MemberData(nameof(UnownedScopes))]
    public async Task Insert_ScopedToUnownedEntity_IsRejectedAsNotFound_AndNothingPersists(int scopeTypeId, string scopeField)
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoListEdit>>();
        var list = await portal.CreateAsync(scopeTypeId, ScopeEntity(scopeField));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => list.SaveAsync());

        Assert.NotNull(UnwrapScopeEntityNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.TodoLists.ToListAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Insert_ScopedToNonexistentEntity_IsRejectedAsNotFound_AndNothingPersists(int scopeTypeId)
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoListEdit>>();
        var list = await portal.CreateAsync(scopeTypeId, Guid.NewGuid());

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => list.SaveAsync());

        Assert.NotNull(UnwrapScopeEntityNotFound(ex));
        var ctx = provider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.TodoLists.ToListAsync());
    }

    [Fact]
    public async Task Fetch_ByScope_ReturnsThePersistedList()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoListEdit>>();
        var created = await portal.CreateAsync(ScopeType.Team.Id, OwnedTeamId);
        created = await created.SaveAsync();

        var fetched = await portal.FetchAsync(ScopeType.Team.Id, OwnedTeamId);

        Assert.Equal(created.Id, fetched.Id);
        Assert.Same(ScopeType.Team, fetched.ScopeType);
        Assert.Equal(OwnedTeamId, fetched.ScopeEntityId);
    }

    [Fact]
    public async Task Fetch_WhenNoListExistsForTheScope_ThrowsListNotFound()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoListEdit>>();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => portal.FetchAsync(ScopeType.League.Id, OwnedLeagueId));

        Assert.NotNull(ex switch
        {
            TodoListNotFoundException notFound => notFound,
            DataPortalException { BusinessException: TodoListNotFoundException notFound } => notFound,
            _ => null,
        });
    }

    [Fact]
    public async Task Create_WithUnknownScopeType_IsInvalid()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoListEdit>>();

        var list = await portal.CreateAsync(99, OwnedLeagueId);

        Assert.False(list.IsValid);
        Assert.Contains(list.BrokenRulesCollection, r => r.Property == nameof(TodoListEdit.ScopeTypeId));
        Assert.Null(list.ScopeType);
    }

    [Fact]
    public async Task Create_WithEmptyScopeEntityId_IsInvalid()
    {
        var provider = await BuildProviderAsync();
        var portal = provider.GetRequiredService<IDataPortal<TodoListEdit>>();

        var list = await portal.CreateAsync(ScopeType.League.Id, Guid.Empty);

        Assert.False(list.IsValid);
        Assert.Contains(list.BrokenRulesCollection, r => r.Property == nameof(TodoListEdit.ScopeEntityId));
    }
}
