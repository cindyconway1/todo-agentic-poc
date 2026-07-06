namespace ToDo.DataAccess;

public interface IAuditable
{
    DateTime CreateDt { get; set; }
    DateTime LastUpdateDt { get; set; }
    Guid? CreateUserId { get; set; }
    Guid? LastUpdateUserId { get; set; }
}
