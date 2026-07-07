using System.Security.Claims;
using Csla;
using Csla.Rules;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Auth;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IDataPortal<UserEdit> _userEditPortal;
    private readonly IDataPortal<LoginCommand> _loginCommandPortal;
    private readonly IAntiforgery _antiforgery;

    public AuthController(
        IDataPortal<UserEdit> userEditPortal,
        IDataPortal<LoginCommand> loginCommandPortal,
        IAntiforgery antiforgery)
    {
        _userEditPortal = userEditPortal;
        _loginCommandPortal = loginCommandPortal;
        _antiforgery = antiforgery;
    }

    [HttpPost("register")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var user = await _userEditPortal.CreateAsync();
        user.Email = request.Email;
        user.Password = request.Password;

        if (!user.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(user.BrokenRulesCollection));
        }

        try
        {
            user = await user.SaveAsync();
        }
        catch (Exception ex) when (UnwrapDuplicateEmail(ex) is not null)
        {
            return Conflict(new MessageDto { Message = "Email already in use." });
        }

        return CreatedAtAction(nameof(Me), null, new UserDto { Id = user.Id, Email = user.Email });
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
        {
            return UnprocessableEntity(new ValidationProblemDto
            {
                Id = Guid.NewGuid().ToString(),
                Message = "Validation failed.",
                Errors =
                [
                    new ValidationErrorDto { Property = "email", Message = "Email and password are required." },
                ],
            });
        }

        var result = await _loginCommandPortal.ExecuteAsync(request.Email, request.Password);

        if (!result.Succeeded)
        {
            return Unauthorized(new MessageDto { Message = "Invalid email or password." });
        }

        var claims = new List<Claim>
        {
            new(AuthClaimTypes.UserId, result.UserId!.Value.ToString()),
            new(AuthClaimTypes.Email, result.Email!),
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return NoContent();
    }

    [HttpPost("logout")]
    [Authorize(Policy = AuthPolicies.AuthenticatedUser)]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize(Policy = AuthPolicies.AuthenticatedUser)]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        var idClaim = User.FindFirst(AuthClaimTypes.UserId)?.Value;
        var emailClaim = User.FindFirst(AuthClaimTypes.Email)?.Value;
        if (idClaim is null || emailClaim is null || !Guid.TryParse(idClaim, out var id))
        {
            return Unauthorized();
        }

        return Ok(new UserDto { Id = id, Email = emailClaim });
    }

    [HttpGet("antiforgery")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult GetAntiforgeryToken()
    {
        var tokens = _antiforgery.GetAndStoreTokens(HttpContext);
        Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
        });

        return NoContent();
    }

    private static ValidationProblemDto BuildValidationProblem(BrokenRulesCollection brokenRules)
    {
        return new ValidationProblemDto
        {
            Id = Guid.NewGuid().ToString(),
            Message = "Validation failed.",
            Errors = brokenRules
                .Where(r => r.Severity == RuleSeverity.Error)
                .Select(r => new ValidationErrorDto { Property = r.Property, Message = r.Description })
                .ToList(),
            Warnings = brokenRules
                .Where(r => r.Severity == RuleSeverity.Warning)
                .Select(r => new ValidationErrorDto { Property = r.Property, Message = r.Description })
                .ToList(),
        };
    }

    private static DuplicateEmailException? UnwrapDuplicateEmail(Exception ex) => ex switch
    {
        DuplicateEmailException dup => dup,
        DataPortalException { BusinessException: DuplicateEmailException dup } => dup,
        _ => null,
    };
}
