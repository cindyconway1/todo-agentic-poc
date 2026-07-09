using Csla;

namespace ToDo.Business;

/// <summary>
/// One entity (league/team/volunteer) on the dashboard with its lists. Only entities with at
/// least one list appear (see <see cref="DashboardInfo"/>).
/// </summary>
[Serializable]
public class DashboardGroupInfo : ReadOnlyBase<DashboardGroupInfo>
{
    public static readonly PropertyInfo<Guid> EntityIdProperty = RegisterProperty<Guid>(c => c.EntityId);
    public Guid EntityId
    {
        get => GetProperty(EntityIdProperty);
        private set => LoadProperty(EntityIdProperty, value);
    }

    public static readonly PropertyInfo<string> EntityNameProperty = RegisterProperty<string>(c => c.EntityName, "EntityName", "");
    public string EntityName
    {
        get => GetProperty(EntityNameProperty) ?? "";
        private set => LoadProperty(EntityNameProperty, value);
    }

    public static readonly PropertyInfo<DashboardListInfoList> ListsProperty = RegisterProperty<DashboardListInfoList>(c => c.Lists);
    public DashboardListInfoList Lists
    {
        get => GetProperty(ListsProperty)!;
        private set => LoadProperty(ListsProperty, value);
    }

    [FetchChild]
    private void FetchChild(
        DashboardGroupData group,
        [Inject] IChildDataPortal<DashboardListInfoList> listsPortal)
    {
        EntityId = group.EntityId;
        EntityName = group.EntityName;
        Lists = listsPortal.FetchChild(group.Lists);
    }
}
