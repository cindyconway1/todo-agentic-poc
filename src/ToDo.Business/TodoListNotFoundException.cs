namespace ToDo.Business;

// A fetch miss for a scope entity's implicit list. In the get-or-create flow this is the
// "first access" signal — the caller responds by creating the list, not by returning an error.
public sealed class TodoListNotFoundException : Exception
{
    public TodoListNotFoundException(int scopeTypeId, Guid scopeEntityId)
        : base($"No to-do list exists for scope entity '{scopeEntityId}' (scope type {scopeTypeId}).")
    {
        ScopeTypeId = scopeTypeId;
        ScopeEntityId = scopeEntityId;
    }

    public int ScopeTypeId { get; }
    public Guid ScopeEntityId { get; }
}
