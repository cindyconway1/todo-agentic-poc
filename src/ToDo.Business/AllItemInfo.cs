using Csla;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// One row of the All-Items view (BE-08, AC 29): an incomplete item labeled with its source
/// list and scope entity. Lists are implicit one-per-entity with no name column, so ListName
/// and ScopeName are both the owning entity's name.
/// </summary>
[Serializable]
public class AllItemInfo : ReadOnlyBase<AllItemInfo>
{
    public static readonly PropertyInfo<Guid> IdProperty = RegisterProperty<Guid>(c => c.Id);
    public Guid Id
    {
        get => GetProperty(IdProperty);
        private set => LoadProperty(IdProperty, value);
    }

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

    public static readonly PropertyInfo<string> ScopeTypeNameProperty = RegisterProperty<string>(c => c.ScopeTypeName, "ScopeTypeName", "");
    public string ScopeTypeName
    {
        get => GetProperty(ScopeTypeNameProperty) ?? "";
        private set => LoadProperty(ScopeTypeNameProperty, value);
    }

    public static readonly PropertyInfo<string> ScopeNameProperty = RegisterProperty<string>(c => c.ScopeName, "ScopeName", "");
    public string ScopeName
    {
        get => GetProperty(ScopeNameProperty) ?? "";
        private set => LoadProperty(ScopeNameProperty, value);
    }

    public static readonly PropertyInfo<string> TitleProperty = RegisterProperty<string>(c => c.Title, "Title", "");
    public string Title
    {
        get => GetProperty(TitleProperty) ?? "";
        private set => LoadProperty(TitleProperty, value);
    }

    public static readonly PropertyInfo<string?> DescriptionProperty = RegisterProperty<string?>(c => c.Description);
    public string? Description
    {
        // CSLA coerces a null string managed field to "" — normalize back so an absent
        // description surfaces as null in the DTO.
        get => string.IsNullOrEmpty(GetProperty(DescriptionProperty)) ? null : GetProperty(DescriptionProperty);
        private set => LoadProperty(DescriptionProperty, value);
    }

    public static readonly PropertyInfo<int?> PriorityIdProperty = RegisterProperty<int?>(c => c.PriorityId);
    public int? PriorityId
    {
        get => GetProperty(PriorityIdProperty);
        private set => LoadProperty(PriorityIdProperty, value);
    }

    public static readonly PropertyInfo<string?> PriorityNameProperty = RegisterProperty<string?>(c => c.PriorityName);
    public string? PriorityName
    {
        // CSLA coerces a null string managed field to "" — normalize back so an absent
        // priority surfaces as null in the DTO.
        get => string.IsNullOrEmpty(GetProperty(PriorityNameProperty)) ? null : GetProperty(PriorityNameProperty);
        private set => LoadProperty(PriorityNameProperty, value);
    }

    public static readonly PropertyInfo<DateOnly?> DueDateProperty = RegisterProperty<DateOnly?>(c => c.DueDate);
    public DateOnly? DueDate
    {
        get => GetProperty(DueDateProperty);
        private set => LoadProperty(DueDateProperty, value);
    }

    [FetchChild]
    private void FetchChild(TodoItem entity, string? priorityName, string scopeTypeName, string entityName)
    {
        Id = entity.Id;
        ListId = entity.ListId;
        ListName = entityName;
        ScopeTypeName = scopeTypeName;
        ScopeName = entityName;
        Title = entity.Title;
        Description = entity.Description;
        PriorityId = entity.PriorityId;
        // Passed alongside the entity because AllItemsList projects rows (no nav fix-up).
        PriorityName = priorityName;
        DueDate = entity.DueDate;
    }
}
