using Csla;
using Csla.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Auth;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.Api.Controllers;

[ApiController]
[Route("api/leagues")]
[Authorize(Policy = AuthPolicies.AuthenticatedUser)]
public class LeaguesController : ControllerBase
{
    private readonly IDataPortal<LeagueEdit> _leagueEditPortal;
    private readonly IDataPortal<LeagueInfoList> _leagueInfoListPortal;

    public LeaguesController(
        IDataPortal<LeagueEdit> leagueEditPortal,
        IDataPortal<LeagueInfoList> leagueInfoListPortal)
    {
        _leagueEditPortal = leagueEditPortal;
        _leagueInfoListPortal = leagueInfoListPortal;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<LeagueDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List()
    {
        var leagues = await _leagueInfoListPortal.FetchAsync();

        return Ok(leagues.Select(l => new LeagueDto { Id = l.Id, Name = l.Name }).ToList());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(LeagueDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Create([FromBody] CreateLeagueRequest request)
    {
        var league = await _leagueEditPortal.CreateAsync();
        league.Name = request.Name ?? "";

        if (!league.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(league.BrokenRulesCollection));
        }

        league = await league.SaveAsync();

        return CreatedAtAction(nameof(Get), new { id = league.Id }, new LeagueDto { Id = league.Id, Name = league.Name });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(LeagueDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id)
    {
        LeagueEdit league;
        try
        {
            league = await _leagueEditPortal.FetchAsync(id);
        }
        catch (Exception ex) when (UnwrapNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "League not found." });
        }

        return Ok(new LeagueDto { Id = league.Id, Name = league.Name });
    }

    [HttpPut("{id:guid}")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(LeagueDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateLeagueRequest request)
    {
        LeagueEdit league;
        try
        {
            league = await _leagueEditPortal.FetchAsync(id);
        }
        catch (Exception ex) when (UnwrapNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "League not found." });
        }

        league.Name = request.Name ?? "";

        if (!league.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(league.BrokenRulesCollection));
        }

        league = await league.SaveAsync();

        return Ok(new LeagueDto { Id = league.Id, Name = league.Name });
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
            await _leagueEditPortal.DeleteAsync(id);
        }
        catch (Exception ex) when (UnwrapNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "League not found." });
        }

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

    private static LeagueNotFoundException? UnwrapNotFound(Exception ex) => ex switch
    {
        LeagueNotFoundException notFound => notFound,
        DataPortalException { BusinessException: LeagueNotFoundException notFound } => notFound,
        _ => null,
    };
}
