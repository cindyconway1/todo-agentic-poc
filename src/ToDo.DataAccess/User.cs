namespace ToDo.DataAccess;

public class User : AuditableEntity
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
}
