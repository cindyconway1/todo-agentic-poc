using Csla;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// One row of the Priorities lookup (BE-10): a seeded reference value (1 High, 2 Medium,
/// 3 Low) the frontend uses to populate the priority dropdown.
/// </summary>
[Serializable]
public class PriorityInfo : ReadOnlyBase<PriorityInfo>
{
    public static readonly PropertyInfo<int> IdProperty = RegisterProperty<int>(c => c.Id);
    public int Id
    {
        get => GetProperty(IdProperty);
        private set => LoadProperty(IdProperty, value);
    }

    public static readonly PropertyInfo<string> NameProperty = RegisterProperty<string>(c => c.Name, "Name", "");
    public string Name
    {
        get => GetProperty(NameProperty) ?? "";
        private set => LoadProperty(NameProperty, value);
    }

    public static readonly PropertyInfo<int> SortOrderProperty = RegisterProperty<int>(c => c.SortOrder);
    public int SortOrder
    {
        get => GetProperty(SortOrderProperty);
        private set => LoadProperty(SortOrderProperty, value);
    }

    [FetchChild]
    private void FetchChild(Priority entity)
    {
        Id = entity.Id;
        Name = entity.Name;
        SortOrder = entity.SortOrder;
    }
}
