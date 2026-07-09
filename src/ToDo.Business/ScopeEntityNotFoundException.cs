namespace ToDo.Business;

// Also thrown for scope entities owned by another user, and for unknown scope types: per AC 11/20
// a cross-owner or nonexistent scope entity must be indistinguishable (404, never 403).
public sealed class ScopeEntityNotFoundException : Exception
{
    public ScopeEntityNotFoundException(int scopeTypeId, Guid scopeEntityId)
        : base($"Scope entity '{scopeEntityId}' (scope type {scopeTypeId}) was not found.")
    {
        ScopeTypeId = scopeTypeId;
        ScopeEntityId = scopeEntityId;
    }

    public int ScopeTypeId { get; }
    public Guid ScopeEntityId { get; }
}
