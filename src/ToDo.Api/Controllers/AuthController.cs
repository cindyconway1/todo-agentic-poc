using System.Security.Claims;
using Csla;
using Csla.Rules;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Dtos;
using ToDo.Api.Security;
using ToDo.Business;
using ToDo.Business.Exceptions;

namespace ToDo.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    [HttpPost("register")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationErrorDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register(RegisterRequest request, [FromServices] IDataPortal<UserEdit> portal)
    {
        var user = await portal.CreateAsync();
        user.Email = request.Email;
        user.Password = request.Password;

        if (!user.IsValid)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, BuildValidationError(user));
        }

        try
        {
            user = await user.SaveAsync();
        }
        catch (DuplicateEmailException)
        {
            return Conflict(new ProblemDetails { Title = "Email already in use.", Status = StatusCodes.Status409Conflict });
        }
        catch (DataPortalException ex) when (ex.InnerException is DuplicateEmailException)
        {
            return Conflict(new ProblemDetails { Title = "Email already in use.", Status = StatusCodes.Status409Conflict });
        }

        return StatusCode(StatusCodes.Status201Created, new UserDto(user.Id, user.Email));
    }

    [HttpPost("login")]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login(LoginRequest request, [FromServices] IDataPortal<LoginCommand> portal)
    {
        var command = await portal.CreateAsync();
        command.Email = request.Email;
        command.Password = request.Password;
        command = await portal.ExecuteAsync(command);

        if (!command.Success)
        {
            return Unauthorized(new ProblemDetails { Title = "Invalid email or password.", Status = StatusCodes.Status401Unauthorized });
        }

        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(AuthClaimTypes.UserId, command.UserId!.Value.ToString()),
                new Claim(AuthClaimTypes.Email, command.UserEmail!)
            },
            CookieAuthenticationDefaults.AuthenticationScheme);

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
        var userId = User.FindFirstValue(AuthClaimTypes.UserId);
        var email = User.FindFirstValue(AuthClaimTypes.Email);
        return Ok(new UserDto(Guid.Parse(userId!), email!));
    }

    [HttpGet("antiforgery")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Antiforgery([FromServices] IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict
        });
        return NoContent();
    }

    private static ValidationErrorDto BuildValidationError(UserEdit user)
    {
        var errors = user.BrokenRulesCollection
            .Where(r => r.Severity == RuleSeverity.Error)
            .Select(r => r.Description)
            .ToList();
        var warnings = user.BrokenRulesCollection
            .Where(r => r.Severity == RuleSeverity.Warning)
            .Select(r => r.Description)
            .ToList();

        return new ValidationErrorDto(Guid.NewGuid().ToString(), "Validation failed.", errors, warnings);
    }
}
