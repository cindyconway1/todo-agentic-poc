using Csla;
using Csla.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Auth;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.Api.Controllers;

[ApiController]
[Route("api/teams")]
[Authorize(Policy = AuthPolicies.AuthenticatedUser)]
public class TeamsController : ControllerBase
{
    private readonly IDataPortal<TeamEdit> _teamEditPortal;
    private readonly IDataPortal<TeamInfoList> _teamInfoListPortal;

    public TeamsController(
        IDataPortal<TeamEdit> teamEditPortal,
        IDataPortal<TeamInfoList> teamInfoListPortal)
    {
        _teamEditPortal = teamEditPortal;
        _teamInfoListPortal = teamInfoListPortal;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<TeamDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List()
    {
        var teams = await _teamInfoListPortal.FetchAsync();

        return Ok(teams.Select(t => new TeamDto { Id = t.Id, Name = t.Name, LeagueId = t.LeagueId }).ToList());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create([FromBody] CreateTeamRequest request)
    {
        var team = await _teamEditPortal.CreateAsync();
        team.Name = request.Name ?? "";
        team.LeagueId = request.LeagueId;

        if (!team.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(team.BrokenRulesCollection));
        }

        try
        {
            team = await team.SaveAsync();
        }
        catch (Exception ex) when (UnwrapLeagueNotFound(ex) is not null)
        {
            // Tag-ownership (AC 17): an unowned or nonexistent league tag is a 404, never a 422,
            // so the league's existence never leaks.
            return NotFound(new MessageDto { Message = "League not found." });
        }

        return CreatedAtAction(nameof(Get), new { id = team.Id }, ToDto(team));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id)
    {
        TeamEdit team;
        try
        {
            team = await _teamEditPortal.FetchAsync(id);
        }
        catch (Exception ex) when (UnwrapTeamNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "Team not found." });
        }

        return Ok(ToDto(team));
    }

    [HttpPut("{id:guid}")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(TeamDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTeamRequest request)
    {
        TeamEdit team;
        try
        {
            team = await _teamEditPortal.FetchAsync(id);
        }
        catch (Exception ex) when (UnwrapTeamNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "Team not found." });
        }

        team.Name = request.Name ?? "";
        team.LeagueId = request.LeagueId;

        if (!team.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(team.BrokenRulesCollection));
        }

        try
        {
            team = await team.SaveAsync();
        }
        catch (Exception ex) when (UnwrapLeagueNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "League not found." });
        }

        return Ok(ToDto(team));
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
            await _teamEditPortal.DeleteAsync(id);
        }
        catch (Exception ex) when (UnwrapTeamNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "Team not found." });
        }

        return NoContent();
    }

    private static TeamDto ToDto(TeamEdit team) =>
        new() { Id = team.Id, Name = team.Name, LeagueId = team.LeagueId };

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

    private static TeamNotFoundException? UnwrapTeamNotFound(Exception ex) => ex switch
    {
        TeamNotFoundException notFound => notFound,
        DataPortalException { BusinessException: TeamNotFoundException notFound } => notFound,
        _ => null,
    };

    private static LeagueNotFoundException? UnwrapLeagueNotFound(Exception ex) => ex switch
    {
        LeagueNotFoundException notFound => notFound,
        DataPortalException { BusinessException: LeagueNotFoundException notFound } => notFound,
        _ => null,
    };
}
