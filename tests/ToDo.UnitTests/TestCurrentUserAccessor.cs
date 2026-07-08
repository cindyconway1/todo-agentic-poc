using ToDo.DataAccess;

namespace ToDo.UnitTests;

/// <summary>Test double for the audit-stamping current-user accessor.</summary>
internal sealed class TestCurrentUserAccessor : ICurrentUserAccessor
{
    public Guid? CurrentUserId { get; }

    public TestCurrentUserAccessor(Guid? userId) => CurrentUserId = userId;
}
