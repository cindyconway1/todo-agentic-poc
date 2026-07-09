using Csla;

namespace ToDo.Business;

/// <summary>
/// One list on the dashboard with its incomplete items, pre-sorted. Lists are implicit
/// one-per-entity with no name column, so ListName is the owning entity's name. A list with
/// zero incomplete items still appears — Items is just empty.
/// </summary>
[Serializable]
public class DashboardListInfo : ReadOnlyBase<DashboardListInfo>
{
    public static readonly PropertyInfo<Guid> ListIdProperty = RegisterProperty<Guid>(c => c.ListId);
    public Guid ListId
    {
        get => GetProperty(ListIdProperty);
        private set => LoadProperty(ListIdProperty, value);
    }

    public static readonly PropertyInfo<string> ListNameProperty = RegisterProperty<string>(c => c.ListName, "ListName", "");
    public string ListName
    {
        get => GetProperty(ListNameProperty) ?? "";
        private set => LoadProperty(ListNameProperty, value);
    }

    public static readonly PropertyInfo<TodoItemInfoList> ItemsProperty = RegisterProperty<TodoItemInfoList>(c => c.Items);
    public TodoItemInfoList Items
    {
        get => GetProperty(ItemsProperty)!;
        private set => LoadProperty(ItemsProperty, value);
    }

    [FetchChild]
    private void FetchChild(
        DashboardListData list,
        [Inject] IChildDataPortal<TodoItemInfoList> itemsPortal)
    {
        ListId = list.ListId;
        ListName = list.ListName;
        Items = itemsPortal.FetchChild(list.Items);
    }
}
