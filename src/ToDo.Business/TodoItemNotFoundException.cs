namespace ToDo.Business;

// Also thrown for items owned by another user and for completed items: per AC 11/25 a
// cross-owner item — or one that has been completed and is therefore hidden from every
// view — must be indistinguishable from a nonexistent one (404, never 403).
public sealed class TodoItemNotFoundException : Exception
{
    public TodoItemNotFoundException(Guid itemId)
        : base($"To-do item '{itemId}' was not found.")
    {
        ItemId = itemId;
    }

    public Guid ItemId { get; }
}
