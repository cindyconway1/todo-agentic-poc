using Csla;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Auth;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.Api.Controllers;

/// <summary>
/// Each League/Team/Volunteer has exactly one implicit to-do list — users never create, name,
/// or delete lists directly, so there is no list CRUD here. The single operation is
/// get-or-create: return the entity's list, creating it on first access. The unique
/// (ScopeType, ScopeEntityId) index makes that idempotent, so a plain GET (no antiforgery)
/// is safe — the only side effect is materializing the caller's own empty list.
/// </summary>
[ApiController]
[Route("api/lists")]
[Authorize(Policy = AuthPolicies.AuthenticatedUser)]
public class ListsController : ControllerBase
{
    private readonly IDataPortal<TodoListEdit> _todoListPortal;

    public ListsController(IDataPortal<TodoListEdit> todoListPortal)
    {
        _todoListPortal = todoListPortal;
    }

    [HttpGet("{scopeTypeName}/{scopeEntityId:guid}")]
    [ProducesResponseType(typeof(TodoListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrCreate(string scopeTypeName, Guid scopeEntityId)
    {
        // An unknown scope type is a 404 like any other nonexistent resource path.
        if (ScopeType.TryFromName(scopeTypeName) is not ScopeType scopeType)
        {
            return NotFound(new MessageDto { Message = "Scope entity not found." });
        }

        try
        {
            var existing = await _todoListPortal.FetchAsync(scopeType.Id, scopeEntityId);
            return Ok(ToDto(existing));
        }
        catch (Exception ex) when (UnwrapListNotFound(ex) is not null)
        {
            // First access — fall through and create the implicit list.
        }

        var list = await _todoListPortal.CreateAsync(scopeType.Id, scopeEntityId);
        try
        {
            list = await list.SaveAsync();
        }
        catch (Exception ex) when (UnwrapScopeEntityNotFound(ex) is not null)
        {
            // Unowned and nonexistent scope entities are both 404 so existence never leaks (AC 11/20).
            return NotFound(new MessageDto { Message = "Scope entity not found." });
        }
        catch (Exception ex) when (UnwrapAlreadyExists(ex) is not null)
        {
            // Lost the get-or-create race to a concurrent first access; the list now exists.
            list = await _todoListPortal.FetchAsync(scopeType.Id, scopeEntityId);
        }

        return Ok(ToDto(list));
    }

    private static TodoListDto ToDto(TodoListEdit list) => new()
    {
        Id = list.Id,
        ScopeType = list.ScopeType?.Name ?? "",
        ScopeEntityId = list.ScopeEntityId,
    };

    private static TodoListNotFoundException? UnwrapListNotFound(Exception ex) => ex switch
    {
        TodoListNotFoundException notFound => notFound,
        DataPortalException { BusinessException: TodoListNotFoundException notFound } => notFound,
        _ => null,
    };

    private static ScopeEntityNotFoundException? UnwrapScopeEntityNotFound(Exception ex) => ex switch
    {
        ScopeEntityNotFoundException notFound => notFound,
        DataPortalException { BusinessException: ScopeEntityNotFoundException notFound } => notFound,
        _ => null,
    };

    private static TodoListAlreadyExistsException? UnwrapAlreadyExists(Exception ex) => ex switch
    {
        TodoListAlreadyExistsException exists => exists,
        DataPortalException { BusinessException: TodoListAlreadyExistsException exists } => exists,
        _ => null,
    };
}
