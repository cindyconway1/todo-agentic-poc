namespace ToDo.DataAccess;

/// <summary>
/// The single implicit to-do list for one scope entity. Each League/Team/Volunteer has exactly
/// one list (unique (ScopeTypeId, ScopeEntityId) index); users never create, name, or delete
/// lists directly — the list is get-or-create via the API.
/// </summary>
public class TodoList : AuditableEntity
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    // The ScopeType TypeID id (League/Team/Volunteer — see ToDo.Business.ScopeType).
    public int ScopeTypeId { get; set; }
    // Deliberately no FK: this points at Leagues, Teams, or Volunteers depending on ScopeTypeId,
    // which a single foreign key cannot express. Existence + ownership of the referenced entity
    // are enforced at the data portal in TodoListEdit instead.
    public Guid ScopeEntityId { get; set; }
}
