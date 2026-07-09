using Csla;
using Csla.Core;
using Csla.Rules.CommonRules;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class VolunteerEdit : BusinessBase<VolunteerEdit>
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

    // Team tags as a set of ids: the join rows carry no fields of their own, so a GUID set
    // reconciled in the data portal replaces a full CSLA child collection. MobileList (not
    // List<Guid>) because the local data portal clones via MobileFormatter, which silently drops
    // managed fields that aren't primitives or IMobileObject.
    public static readonly PropertyInfo<MobileList<Guid>> TeamIdsProperty =
        RegisterProperty<MobileList<Guid>>(nameof(TeamIds));
    public IReadOnlyList<Guid> TeamIds
    {
        get => GetProperty(TeamIdsProperty) ?? [];
        set => SetProperty(TeamIdsProperty, new MobileList<Guid>(value.Distinct().ToList()));
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

    // Tag-ownership (AC 17): every tagged team must be owned by the current user. Any unowned or
    // nonexistent team rejects the whole operation as a not-found, so no tags are applied and the
    // team's existence never leaks.
    private static async Task EnsureTeamTagsAreOwnedAsync(
        IReadOnlyCollection<Guid> teamIds, Guid? currentUserId, ApplicationDbContext dbContext)
    {
        if (teamIds.Count == 0)
        {
            return;
        }

        var ownedIds = await dbContext.Teams
            .AsNoTracking()
            .Where(t => teamIds.Contains(t.Id) && t.OwnerUserId == currentUserId)
            .Select(t => t.Id)
            .ToListAsync();
        var unowned = teamIds.Except(ownedIds).ToList();
        if (unowned.Count > 0)
        {
            throw new TeamNotFoundException(unowned[0]);
        }
    }

    [Create]
    private void Create()
    {
        Id = Guid.NewGuid();
        LoadProperty(TeamIdsProperty, new MobileList<Guid>());
        BusinessRules.CheckRules();
    }

    [Fetch]
    private async Task FetchAsync(
        Guid id,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Owner-scoped fetch (defense-in-depth): another user's volunteer is a miss, never a 403.
        var entity = await dbContext.Volunteers
            .AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == id && v.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new VolunteerNotFoundException(id);

        var teamIds = await dbContext.VolunteerTeams
            .AsNoTracking()
            .Where(vt => vt.VolunteerId == id)
            .Select(vt => vt.TeamId)
            .ToListAsync();

        using (BypassPropertyChecks)
        {
            Id = entity.Id;
            Name = entity.Name;
            LeagueId = entity.LeagueId;
            LoadProperty(TeamIdsProperty, new MobileList<Guid>(teamIds));
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
            ?? throw new InvalidOperationException("An authenticated user is required to create a volunteer.");

        var teamIds = TeamIds;
        await EnsureLeagueTagIsOwnedAsync(LeagueId, ownerUserId, dbContext);
        await EnsureTeamTagsAreOwnedAsync(teamIds, ownerUserId, dbContext);

        var entity = new Volunteer
        {
            Id = Id,
            OwnerUserId = ownerUserId,
            Name = Name.Trim(),
            LeagueId = LeagueId,
        };

        dbContext.Volunteers.Add(entity);
        foreach (var teamId in teamIds)
        {
            dbContext.VolunteerTeams.Add(new VolunteerTeam { VolunteerId = Id, TeamId = teamId });
        }
        await dbContext.SaveChangesAsync();
    }

    [Update]
    private async Task UpdateAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        var entity = await dbContext.Volunteers
            .SingleOrDefaultAsync(v => v.Id == Id && v.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new VolunteerNotFoundException(Id);

        var teamIds = TeamIds;
        await EnsureLeagueTagIsOwnedAsync(LeagueId, currentUser.CurrentUserId, dbContext);
        await EnsureTeamTagsAreOwnedAsync(teamIds, currentUser.CurrentUserId, dbContext);

        entity.Name = Name.Trim();
        entity.LeagueId = LeagueId;

        // Reconcile the join rows to match the supplied set: remove absent, add missing.
        var existing = await dbContext.VolunteerTeams
            .Where(vt => vt.VolunteerId == Id)
            .ToListAsync();
        dbContext.VolunteerTeams.RemoveRange(existing.Where(vt => !teamIds.Contains(vt.TeamId)));
        var existingTeamIds = existing.Select(vt => vt.TeamId).ToHashSet();
        foreach (var teamId in teamIds.Where(id => !existingTeamIds.Contains(id)))
        {
            dbContext.VolunteerTeams.Add(new VolunteerTeam { VolunteerId = Id, TeamId = teamId });
        }

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
        // The VolunteerTeams FK cascades, so the tag rows go with the volunteer.
        var entity = await dbContext.Volunteers
            .SingleOrDefaultAsync(v => v.Id == id && v.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new VolunteerNotFoundException(id);

        dbContext.Volunteers.Remove(entity);
        await dbContext.SaveChangesAsync();
    }
}
