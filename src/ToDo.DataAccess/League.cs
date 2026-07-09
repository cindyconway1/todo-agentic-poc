namespace ToDo.DataAccess;

public class League : AuditableEntity
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = "";
}
