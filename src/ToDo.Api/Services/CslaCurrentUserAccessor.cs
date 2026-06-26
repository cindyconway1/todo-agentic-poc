using Csla;
using System.Security.Claims;
using ToDo.DataAccess;

namespace ToDo.Api.Services;

public sealed class CslaCurrentUserAccessor : ICurrentUserAccessor
{
    private readonly ApplicationContext _applicationContext;

    public CslaCurrentUserAccessor(ApplicationContext applicationContext)
    {
        _applicationContext = applicationContext;
    }

    public Guid? CurrentUserId
    {
        get
        {
            var value = _applicationContext.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return value is not null && Guid.TryParse(value, out var id) ? id : null;
        }
    }
}
