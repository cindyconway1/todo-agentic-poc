using Csla;

namespace ToDo.Business;

/// <summary>
/// One dashboard scope group (Leagues, Teams, or People): the entities of that scope type that
/// have at least one list, ordered by entity name. Child-only — <see cref="DashboardInfo"/>
/// runs the queries and hands each group its pre-assembled rows.
/// </summary>
[Serializable]
public class DashboardGroupList : ReadOnlyListBase<DashboardGroupList, DashboardGroupInfo>
{
    [FetchChild]
    private void FetchChild(
        List<DashboardGroupData> groups,
        [Inject] IChildDataPortal<DashboardGroupInfo> groupPortal)
    {
        using (LoadListMode)
        {
            foreach (var group in groups)
            {
                Add(groupPortal.FetchChild(group));
            }
        }
    }
}
