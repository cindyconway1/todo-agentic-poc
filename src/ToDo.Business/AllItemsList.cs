using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// The flat All-Items read model (BE-08, AC 29): every incomplete item across all of the
/// current user's lists, sorted in the query (AC 26/27 order: DueDate ascending, nulls last,
/// CreateDt tiebreak), each row labeled with its source list and scope entity. Assembled in
/// four fixed queries regardless of how many lists/items exist — no per-list query in a loop.
/// </summary>
[Serializable]
public class AllItemsList : ReadOnlyListBase<AllItemsList, AllItemInfo>
{
    [Fetch]
    private async Task FetchAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser,
        [Inject] IChildDataPortal<AllItemInfo> itemInfoPortal)
    {
        // Owner-scoped: the user id comes from the CSLA context, never the request.
        var userId = currentUser.CurrentUserId;

        // Query 1: items joined to their list's scope, filtered and sorted in SQL — the
        // (OwnerUserId, IsCompleted, DueDate) index from BE-07 serves this shape.
        var rows = await (
            from item in dbContext.TodoItems.AsNoTracking()
            join list in dbContext.TodoLists.AsNoTracking() on item.ListId equals list.Id
            where item.OwnerUserId == userId && !item.IsCompleted
            orderby item.DueDate == null, item.DueDate, item.CreateDt
            select new { Item = item, list.ScopeTypeId, list.ScopeEntityId })
            .ToListAsync();

        // Queries 2–4: the owner's entity-name maps, resolved once each.
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

        using (LoadListMode)
        {
            foreach (var row in rows)
            {
                if (ScopeType.TryFromId(row.ScopeTypeId) is not ScopeType scopeType)
                {
                    continue;
                }

                var entityNames = scopeType.Id == ScopeType.League.Id ? leagueNames
                    : scopeType.Id == ScopeType.Team.Id ? teamNames
                    : volunteerNames;

                // A list whose scope entity row is gone (ScopeEntityId has no FK) drops out,
                // matching the dashboard's inner-join semantics.
                if (!entityNames.TryGetValue(row.ScopeEntityId, out var entityName))
                {
                    continue;
                }

                Add(itemInfoPortal.FetchChild(row.Item, scopeType.Name, entityName));
            }
        }
    }
}
