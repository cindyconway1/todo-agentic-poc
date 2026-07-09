using Csla;
using Csla.Rules.CommonRules;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class TeamEdit : BusinessBase<TeamEdit>
{
    public const int MaxNameLength = 100;

    public static readonly PropertyInfo<Guid> IdProperty = RegisterProperty<Guid>(c => c.Id);
    public Guid Id
    {
        get => GetProperty(IdProperty);
        private set => LoadProperty(IdProperty, value);
    }

    public static readonly PropertyInfo<string> NameProperty = RegisterProperty<string>(c => c.Name, "Name", "");
    public string Name
    {
        get => GetProperty(NameProperty) ?? "";
        set => SetProperty(NameProperty, value);
    }

    public static readonly PropertyInfo<Guid?> LeagueIdProperty = RegisterProperty<Guid?>(c => c.LeagueId);
    public Guid? LeagueId
    {
        get => GetProperty(LeagueIdProperty);
        set => SetProperty(LeagueIdProperty, value);
    }

    protected override void AddBusinessRules()
    {
        base.AddBusinessRules();

        BusinessRules.AddRule(new Required(NameProperty));
        BusinessRules.AddRule(new MaxLength(NameProperty, MaxNameLength));
    }

    // Tag-ownership (AC 17): a set LeagueId must reference a league owned by the current user.
    // Unowned and nonexistent are both a not-found — 404, never 422 — so the league's existence
    // never leaks. Enforced here in the data portal where the owning context is available.
    private static async Task EnsureLeagueTagIsOwnedAsync(
        Guid? leagueId, Guid? currentUserId, ApplicationDbContext dbContext)
    {
        if (leagueId is not Guid id)
        {
            return;
        }

        var owned = await dbContext.Leagues
            .AsNoTracking()
            .AnyAsync(l => l.Id == id && l.OwnerUserId == currentUserId);
        if (!owned)
        {
            throw new LeagueNotFoundException(id);
        }
    }

    [Create]
    private void Create()
    {
        Id = Guid.NewGuid();
        BusinessRules.CheckRules();
    }

    [Fetch]
    private async Task FetchAsync(
        Guid id,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Owner-scoped fetch (defense-in-depth): another user's team is a miss, never a 403.
        var entity = await dbContext.Teams
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == id && t.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new TeamNotFoundException(id);

        using (BypassPropertyChecks)
        {
            Id = entity.Id;
            Name = entity.Name;
            LeagueId = entity.LeagueId;
        }

        BusinessRules.CheckRules();
    }

    [Insert]
    private async Task InsertAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Owner comes from the authenticated context, never from client input.
        var ownerUserId = currentUser.CurrentUserId
            ?? throw new InvalidOperationException("An authenticated user is required to create a team.");

        await EnsureLeagueTagIsOwnedAsync(LeagueId, ownerUserId, dbContext);

        var entity = new Team
        {
            Id = Id,
            OwnerUserId = ownerUserId,
            Name = Name.Trim(),
            LeagueId = LeagueId,
        };

        dbContext.Teams.Add(entity);
        await dbContext.SaveChangesAsync();
    }

    [Update]
    private async Task UpdateAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        var entity = await dbContext.Teams
            .SingleOrDefaultAsync(t => t.Id == Id && t.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new TeamNotFoundException(Id);

        await EnsureLeagueTagIsOwnedAsync(LeagueId, currentUser.CurrentUserId, dbContext);

        entity.Name = Name.Trim();
        entity.LeagueId = LeagueId;
        await dbContext.SaveChangesAsync();
    }

    [Delete]
    private async Task DeleteAsync(
        Guid id,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Entity-delete vs the implicit list: lists are now get-or-create and 1:1 with their
        // entity (BE-06), which makes the original delete-if-has-lists (409) behavior moot — the
        // decision (block vs cascade the implicit list) is tracked in docs/feature.md §11.
        var entity = await dbContext.Teams
            .SingleOrDefaultAsync(t => t.Id == id && t.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new TeamNotFoundException(id);

        dbContext.Teams.Remove(entity);
        await dbContext.SaveChangesAsync();
    }
}
