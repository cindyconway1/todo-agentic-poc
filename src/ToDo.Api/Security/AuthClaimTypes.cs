using System.Security.Claims;

namespace ToDo.Api.Security;

public static class AuthClaimTypes
{
    public static readonly string UserId = ClaimTypes.NameIdentifier;
    public static readonly string Email = ClaimTypes.Email;
}
