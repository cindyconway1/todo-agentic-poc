using Csla;
using ToDo.DataAccess;

namespace ToDo.Business;

[Serializable]
public class TodoItemInfo : ReadOnlyBase<TodoItemInfo>
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

    public static readonly PropertyInfo<DateOnly?> DueDateProperty = RegisterProperty<DateOnly?>(c => c.DueDate);
    public DateOnly? DueDate
    {
        get => GetProperty(DueDateProperty);
        private set => LoadProperty(DueDateProperty, value);
    }

    public static readonly PropertyInfo<bool> IsCompletedProperty = RegisterProperty<bool>(c => c.IsCompleted);
    public bool IsCompleted
    {
        get => GetProperty(IsCompletedProperty);
        private set => LoadProperty(IsCompletedProperty, value);
    }

    public static readonly PropertyInfo<DateTime?> CompletedAtProperty = RegisterProperty<DateTime?>(c => c.CompletedAt);
    public DateTime? CompletedAt
    {
        get => GetProperty(CompletedAtProperty);
        private set => LoadProperty(CompletedAtProperty, value);
    }

    [FetchChild]
    private void FetchChild(TodoItem entity)
    {
        Id = entity.Id;
        ListId = entity.ListId;
        Title = entity.Title;
        Description = entity.Description;
        DueDate = entity.DueDate;
        IsCompleted = entity.IsCompleted;
        CompletedAt = entity.CompletedAt;
    }
}
