using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class TeamInfoList : ReadOnlyListBase<TeamInfoList, TeamInfo>
{
    [Fetch]
    private async Task FetchAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser,
        [Inject] IChildDataPortal<TeamInfo> teamInfoPortal)
    {
        // Owner-scoped listing: only the current user's teams, never anyone else's (AC 13).
        var entities = await dbContext.Teams
            .AsNoTracking()
            .Where(t => t.OwnerUserId == currentUser.CurrentUserId)
            .OrderBy(t => t.Name)
            .ToListAsync();

        using (LoadListMode)
        {
            foreach (var entity in entities)
            {
                Add(teamInfoPortal.FetchChild(entity));
            }
        }
    }
}
