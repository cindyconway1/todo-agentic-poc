using Csla;
using Csla.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Auth;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.Api.Controllers;

[ApiController]
[Route("api/volunteers")]
[Authorize(Policy = AuthPolicies.AuthenticatedUser)]
public class VolunteersController : ControllerBase
{
    private readonly IDataPortal<VolunteerEdit> _volunteerEditPortal;
    private readonly IDataPortal<VolunteerInfoList> _volunteerInfoListPortal;

    public VolunteersController(
        IDataPortal<VolunteerEdit> volunteerEditPortal,
        IDataPortal<VolunteerInfoList> volunteerInfoListPortal)
    {
        _volunteerEditPortal = volunteerEditPortal;
        _volunteerInfoListPortal = volunteerInfoListPortal;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<VolunteerDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List()
    {
        var volunteers = await _volunteerInfoListPortal.FetchAsync();

        return Ok(volunteers
            .Select(v => new VolunteerDto { Id = v.Id, Name = v.Name, LeagueId = v.LeagueId, TeamIds = v.TeamIds.ToList() })
            .ToList());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(VolunteerDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateVolunteerRequest request)
    {
        var volunteer = await _volunteerEditPortal.CreateAsync();
        volunteer.Name = request.Name ?? "";
        volunteer.LeagueId = request.LeagueId;
        volunteer.TeamIds = request.TeamIds ?? [];

        if (!volunteer.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(volunteer.BrokenRulesCollection));
        }

        try
        {
            volunteer = await volunteer.SaveAsync();
        }
        catch (Exception ex) when (UnwrapTagNotFound(ex) is string message)
        {
            // Tag-ownership (AC 17): an unowned or nonexistent league/team tag is a 404, never a
            // 422, so the entity's existence never leaks — and no tags are applied.
            return NotFound(new MessageDto { Message = message });
        }

        return CreatedAtAction(nameof(Get), new { id = volunteer.Id }, ToDto(volunteer));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(VolunteerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id)
    {
        VolunteerEdit volunteer;
        try
        {
            volunteer = await _volunteerEditPortal.FetchAsync(id);
        }
        catch (Exception ex) when (UnwrapVolunteerNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "Volunteer not found." });
        }

        return Ok(ToDto(volunteer));
    }

    [HttpPut("{id:guid}")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(VolunteerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateVolunteerRequest request)
    {
        VolunteerEdit volunteer;
        try
        {
            volunteer = await _volunteerEditPortal.FetchAsync(id);
        }
        catch (Exception ex) when (UnwrapVolunteerNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "Volunteer not found." });
        }

        volunteer.Name = request.Name ?? "";
        volunteer.LeagueId = request.LeagueId;
        volunteer.TeamIds = request.TeamIds ?? [];

        if (!volunteer.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(volunteer.BrokenRulesCollection));
        }

        try
        {
            volunteer = await volunteer.SaveAsync();
        }
        catch (Exception ex) when (UnwrapTagNotFound(ex) is string message)
        {
            return NotFound(new MessageDto { Message = message });
        }

        return Ok(ToDto(volunteer));
    }

    [HttpDelete("{id:guid}")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id)
    {
        try
        {
            await _volunteerEditPortal.DeleteAsync(id);
        }
        catch (Exception ex) when (UnwrapVolunteerNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "Volunteer not found." });
        }

        return NoContent();
    }

    private static VolunteerDto ToDto(VolunteerEdit volunteer) =>
        new() { Id = volunteer.Id, Name = volunteer.Name, LeagueId = volunteer.LeagueId, TeamIds = volunteer.TeamIds.ToList() };

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

    private static VolunteerNotFoundException? UnwrapVolunteerNotFound(Exception ex) => ex switch
    {
        VolunteerNotFoundException notFound => notFound,
        DataPortalException { BusinessException: VolunteerNotFoundException notFound } => notFound,
        _ => null,
    };

    // A save can be rejected by either tag-ownership check; both surface as a 404 message.
    private static string? UnwrapTagNotFound(Exception ex) =>
        (ex is DataPortalException { BusinessException: { } inner } ? inner : ex) switch
        {
            LeagueNotFoundException => "League not found.",
            TeamNotFoundException => "Team not found.",
            _ => null,
        };
}
