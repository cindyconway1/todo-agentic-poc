using Csla;

namespace ToDo.Business;

/// <summary>
/// One dashboard entity's lists, ordered by list name. With the one-implicit-list-per-entity
/// model this currently holds exactly one list, but the shape matches the DashboardDto contract.
/// </summary>
[Serializable]
public class DashboardListInfoList : ReadOnlyListBase<DashboardListInfoList, DashboardListInfo>
{
    [FetchChild]
    private void FetchChild(
        List<DashboardListData> lists,
        [Inject] IChildDataPortal<DashboardListInfo> listPortal)
    {
        using (LoadListMode)
        {
            foreach (var list in lists)
            {
                Add(listPortal.FetchChild(list));
            }
        }
    }
}
