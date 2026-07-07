using System.Security.Claims;

namespace ToDo.Api.Auth;

public static class AuthClaimTypes
{
    public const string UserId = ClaimTypes.NameIdentifier;
    public const string Email = ClaimTypes.Email;
}
