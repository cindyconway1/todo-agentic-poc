namespace ToDo.Business;

// The list an item operation targets doesn't exist or belongs to another user — both are a
// 404 so list existence never leaks. (Distinct from TodoListNotFoundException, which is keyed
// by scope and signals "first access" in the get-or-create flow; this one is keyed by list id
// and is always an error.)
public sealed class TodoItemListNotFoundException : Exception
{
    public TodoItemListNotFoundException(Guid listId)
        : base($"To-do list '{listId}' was not found.")
    {
        ListId = listId;
    }

    public Guid ListId { get; }
}
