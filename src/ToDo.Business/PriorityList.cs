using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// The Priorities lookup (BE-10), pre-sorted by SortOrder — the read model behind
/// GET /api/priorities. Not owner-scoped: reference data is the same for every user.
/// </summary>
[Serializable]
public class PriorityList : ReadOnlyListBase<PriorityList, PriorityInfo>
{
    [Fetch]
    private async Task FetchAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] IChildDataPortal<PriorityInfo> priorityInfoPortal)
    {
        var entities = await dbContext.Priorities
            .AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ToListAsync();

        using (LoadListMode)
        {
            foreach (var entity in entities)
            {
                Add(priorityInfoPortal.FetchChild(entity));
            }
        }
    }
}
