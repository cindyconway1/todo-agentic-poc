using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class VolunteerInfoList : ReadOnlyListBase<VolunteerInfoList, VolunteerInfo>
{
    [Fetch]
    private async Task FetchAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser,
        [Inject] IChildDataPortal<VolunteerInfo> volunteerInfoPortal)
    {
        // Owner-scoped listing: only the current user's volunteers, never anyone else's (AC 13).
        var entities = await dbContext.Volunteers
            .AsNoTracking()
            .Where(v => v.OwnerUserId == currentUser.CurrentUserId)
            .OrderBy(v => v.Name)
            .ToListAsync();

        var volunteerIds = entities.Select(v => v.Id).ToList();
        var tagsByVolunteer = (await dbContext.VolunteerTeams
                .AsNoTracking()
                .Where(vt => volunteerIds.Contains(vt.VolunteerId))
                .ToListAsync())
            .ToLookup(vt => vt.VolunteerId, vt => vt.TeamId);

        using (LoadListMode)
        {
            foreach (var entity in entities)
            {
                Add(volunteerInfoPortal.FetchChild(entity, tagsByVolunteer[entity.Id].ToList()));
            }
        }
    }
}
