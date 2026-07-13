using Csla;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Auth;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.Api.Controllers;

/// <summary>
/// The grouped dashboard read model (BE-08, AC 28). Read-only — the single GET has no side
/// effects, so no antiforgery. Ownership is enforced in the data portal (the query filters by
/// the CSLA-context user), so there is no 404 path: an empty account just gets three empty groups.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize(Policy = AuthPolicies.AuthenticatedUser)]
public class DashboardController : ControllerBase
{
    private readonly IDataPortal<DashboardInfo> _dashboardPortal;

    public DashboardController(IDataPortal<DashboardInfo> dashboardPortal)
    {
        _dashboardPortal = dashboardPortal;
    }

    [HttpGet]
    [ProducesResponseType(typeof(DashboardDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetDashboard()
    {
        var dashboard = await _dashboardPortal.FetchAsync();

        return Ok(new DashboardDto
        {
            Leagues = ToDtos(dashboard.Leagues),
            Teams = ToDtos(dashboard.Teams),
            People = ToDtos(dashboard.People),
        });
    }

    private static List<GroupDto> ToDtos(DashboardGroupList groups) => groups
        .Select(g => new GroupDto
        {
            EntityId = g.EntityId,
            EntityName = g.EntityName,
            Lists = g.Lists.Select(l => new DashboardListDto
            {
                ListId = l.ListId,
                ListName = l.ListName,
                Items = l.Items.Select(ToDto).ToList(),
            }).ToList(),
        })
        .ToList();

    private static TodoItemDto ToDto(TodoItemInfo item) => new()
    {
        Id = item.Id,
        ListId = item.ListId,
        Title = item.Title,
        Description = item.Description,
        PriorityId = item.PriorityId,
        PriorityName = item.PriorityName,
        DueDate = item.DueDate,
        IsCompleted = item.IsCompleted,
        CompletedAt = item.CompletedAt,
    };
}
