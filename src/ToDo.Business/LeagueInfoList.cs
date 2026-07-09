using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class LeagueInfoList : ReadOnlyListBase<LeagueInfoList, LeagueInfo>
{
    [Fetch]
    private async Task FetchAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser,
        [Inject] IChildDataPortal<LeagueInfo> leagueInfoPortal)
    {
        // Owner-scoped listing: only the current user's leagues, never anyone else's (AC 11).
        var entities = await dbContext.Leagues
            .AsNoTracking()
            .Where(l => l.OwnerUserId == currentUser.CurrentUserId)
            .OrderBy(l => l.Name)
            .ToListAsync();

        using (LoadListMode)
        {
            foreach (var entity in entities)
            {
                Add(leagueInfoPortal.FetchChild(entity));
            }
        }
    }
}
