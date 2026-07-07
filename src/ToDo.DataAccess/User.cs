namespace ToDo.DataAccess;

public class User : AuditableEntity
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
}
