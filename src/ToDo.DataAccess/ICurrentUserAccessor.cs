namespace ToDo.DataAccess;

public interface ICurrentUserAccessor
{
    Guid? CurrentUserId { get; }
}
