using Csla;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Auth;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.Api.Controllers;

/// <summary>
/// The Priorities reference lookup (BE-10). Read-only — the single GET has no side effects,
/// so no antiforgery. Not owner-scoped: the seeded rows are the same for every user, but the
/// endpoint still requires an authenticated caller like every other controller.
/// </summary>
[ApiController]
[Route("api/priorities")]
[Authorize(Policy = AuthPolicies.AuthenticatedUser)]
public class PrioritiesController : ControllerBase
{
    private readonly IDataPortal<PriorityList> _priorityListPortal;

    public PrioritiesController(IDataPortal<PriorityList> priorityListPortal)
    {
        _priorityListPortal = priorityListPortal;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<PriorityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListPriorities()
    {
        // Pre-sorted by SortOrder in the data-portal query.
        var priorities = await _priorityListPortal.FetchAsync();

        return Ok(priorities.Select(p => new PriorityDto
        {
            Id = p.Id,
            Name = p.Name,
            SortOrder = p.SortOrder,
        }).ToList());
    }
}
