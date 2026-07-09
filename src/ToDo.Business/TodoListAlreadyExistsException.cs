namespace ToDo.Business;

// Raised when an insert loses the get-or-create race: the unique (ScopeTypeId, ScopeEntityId)
// index rejected the duplicate, meaning the implicit list now exists — the caller should
// re-fetch it, not fail.
public sealed class TodoListAlreadyExistsException : Exception
{
    public TodoListAlreadyExistsException(int scopeTypeId, Guid scopeEntityId)
        : base($"A to-do list already exists for scope entity '{scopeEntityId}' (scope type {scopeTypeId}).")
    {
        ScopeTypeId = scopeTypeId;
        ScopeEntityId = scopeEntityId;
    }

    public int ScopeTypeId { get; }
    public Guid ScopeEntityId { get; }
}
