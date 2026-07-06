namespace ToDo.DataAccess;

public abstract class AuditableEntity : IAuditable
{
    public DateTime CreateDt { get; set; }
    public DateTime LastUpdateDt { get; set; }
    public Guid? CreateUserId { get; set; }
    public Guid? LastUpdateUserId { get; set; }
}
