using Csla;
using Csla.Rules.CommonRules;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class LeagueEdit : BusinessBase<LeagueEdit>
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

    protected override void AddBusinessRules()
    {
        base.AddBusinessRules();

        BusinessRules.AddRule(new Required(NameProperty));
        BusinessRules.AddRule(new MaxLength(NameProperty, MaxNameLength));
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
        // Owner-scoped fetch (defense-in-depth): another user's league is a miss, never a 403.
        var entity = await dbContext.Leagues
            .AsNoTracking()
            .SingleOrDefaultAsync(l => l.Id == id && l.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new LeagueNotFoundException(id);

        using (BypassPropertyChecks)
        {
            Id = entity.Id;
            Name = entity.Name;
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
            ?? throw new InvalidOperationException("An authenticated user is required to create a league.");

        var entity = new League
        {
            Id = Id,
            OwnerUserId = ownerUserId,
            Name = Name.Trim(),
        };

        dbContext.Leagues.Add(entity);
        await dbContext.SaveChangesAsync();
    }

    [Update]
    private async Task UpdateAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        var entity = await dbContext.Leagues
            .SingleOrDefaultAsync(l => l.Id == Id && l.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new LeagueNotFoundException(Id);

        entity.Name = Name.Trim();
        await dbContext.SaveChangesAsync();
    }

    [Delete]
    private async Task DeleteAsync(
        Guid id,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Delete-if-has-lists (409) is deferred to BE-06 — the TodoList table does not exist yet.
        var entity = await dbContext.Leagues
            .SingleOrDefaultAsync(l => l.Id == id && l.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new LeagueNotFoundException(id);

        dbContext.Leagues.Remove(entity);
        await dbContext.SaveChangesAsync();
    }
}
