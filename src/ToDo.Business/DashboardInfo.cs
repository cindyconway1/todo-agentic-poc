using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// The grouped dashboard read model (BE-08, AC 28): the current user's lists grouped
/// Leagues / Teams / People ("People" == Volunteers) → entity → its lists → incomplete items,
/// everything pre-sorted (AC 26/27 order for items; entity then list name ascending for groups).
/// Assembled in five fixed queries regardless of how many entities/lists/items exist — no
/// per-list or per-entity query in a loop.
/// </summary>
[Serializable]
public class DashboardInfo : ReadOnlyBase<DashboardInfo>
{
    public static readonly PropertyInfo<DashboardGroupList> LeaguesProperty = RegisterProperty<DashboardGroupList>(c => c.Leagues);
    public DashboardGroupList Leagues
    {
        get => GetProperty(LeaguesProperty)!;
        private set => LoadProperty(LeaguesProperty, value);
    }

    public static readonly PropertyInfo<DashboardGroupList> TeamsProperty = RegisterProperty<DashboardGroupList>(c => c.Teams);
    public DashboardGroupList Teams
    {
        get => GetProperty(TeamsProperty)!;
        private set => LoadProperty(TeamsProperty, value);
    }

    public static readonly PropertyInfo<DashboardGroupList> PeopleProperty = RegisterProperty<DashboardGroupList>(c => c.People);
    public DashboardGroupList People
    {
        get => GetProperty(PeopleProperty)!;
        private set => LoadProperty(PeopleProperty, value);
    }

    [Fetch]
    private async Task FetchAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser,
        [Inject] IChildDataPortal<DashboardGroupList> groupListPortal)
    {
        // Owner-scoped throughout: the user id comes from the CSLA context, never the request,
        // and every query filters on it — another user's lists/items/entities can never appear.
        var userId = currentUser.CurrentUserId;

        // Five fixed queries: the user's lists, all their incomplete items (sorted once in SQL,
        // spec §7 order: Priority High → Medium → Low → null, then DueDate ascending nulls last,
        // CreateDt tiebreak), and the three entity-name maps. The grouping itself is in-memory,
        // so the query count never grows.
        var lists = await dbContext.TodoLists
            .AsNoTracking()
            .Where(l => l.OwnerUserId == userId)
            .Select(l => new { l.Id, l.ScopeTypeId, l.ScopeEntityId })
            .ToListAsync();

        var itemsByList = (await dbContext.TodoItems
                .AsNoTracking()
                .Where(i => i.OwnerUserId == userId && !i.IsCompleted)
                .OrderBy(i => i.Priority == "High" ? 0 : i.Priority == "Medium" ? 1 : i.Priority == "Low" ? 2 : 3)
                .ThenBy(i => i.DueDate == null)
                .ThenBy(i => i.DueDate)
                .ThenBy(i => i.CreateDt)
                .ToListAsync())
            .ToLookup(i => i.ListId);

        var leagueNames = await dbContext.Leagues
            .AsNoTracking()
            .Where(e => e.OwnerUserId == userId)
            .ToDictionaryAsync(e => e.Id, e => e.Name);
        var teamNames = await dbContext.Teams
            .AsNoTracking()
            .Where(e => e.OwnerUserId == userId)
            .ToDictionaryAsync(e => e.Id, e => e.Name);
        var volunteerNames = await dbContext.Volunteers
            .AsNoTracking()
            .Where(e => e.OwnerUserId == userId)
            .ToDictionaryAsync(e => e.Id, e => e.Name);

        // Groups start from the lists, so an entity with zero lists never appears; a list whose
        // scope entity row is gone (ScopeEntityId has no FK) drops out the same way. A list with
        // zero incomplete items still appears — its items are simply empty (FE-04 renders that).
        // Lists are implicit one-per-entity with no name of their own, so the list is labeled
        // with its entity's name.
        List<DashboardGroupData> BuildGroups(int scopeTypeId, Dictionary<Guid, string> entityNames) =>
            lists
                .Where(l => l.ScopeTypeId == scopeTypeId && entityNames.ContainsKey(l.ScopeEntityId))
                .GroupBy(l => l.ScopeEntityId)
                .Select(g => new DashboardGroupData(
                    g.Key,
                    entityNames[g.Key],
                    g.Select(l => new DashboardListData(l.Id, entityNames[g.Key], itemsByList[l.Id].ToList()))
                        .OrderBy(ld => ld.ListName, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(ld => ld.ListId)
                        .ToList()))
                .OrderBy(gd => gd.EntityName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(gd => gd.EntityId)
                .ToList();

        Leagues = groupListPortal.FetchChild(BuildGroups(ScopeType.League.Id, leagueNames));
        Teams = groupListPortal.FetchChild(BuildGroups(ScopeType.Team.Id, teamNames));
        People = groupListPortal.FetchChild(BuildGroups(ScopeType.Volunteer.Id, volunteerNames));
    }
}
