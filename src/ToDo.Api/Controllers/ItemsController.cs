using Csla;
using Csla.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ToDo.Api.Auth;
using ToDo.Api.Dtos;
using ToDo.Business;

namespace ToDo.Api.Controllers;

/// <summary>
/// To-do item CRUD plus one-way completion (BE-07). Routes span two prefixes — list-scoped
/// (/api/lists/{listId}/items) for listing/creating and item-scoped (/api/items/{id}) for
/// update/delete/complete — so each action carries an absolute route template.
/// Completed items are hidden everywhere: listing filters them out, and update/delete/complete
/// of a completed item is a 404, the same as a nonexistent or another user's item (AC 11/25).
/// </summary>
[ApiController]
[Authorize(Policy = AuthPolicies.AuthenticatedUser)]
public class ItemsController : ControllerBase
{
    private readonly IDataPortal<TodoItemEdit> _itemEditPortal;
    private readonly IDataPortal<TodoItemInfoList> _itemInfoListPortal;
    private readonly IDataPortal<CompleteItemCommand> _completeItemPortal;
    private readonly IDataPortal<AllItemsList> _allItemsPortal;

    public ItemsController(
        IDataPortal<TodoItemEdit> itemEditPortal,
        IDataPortal<TodoItemInfoList> itemInfoListPortal,
        IDataPortal<CompleteItemCommand> completeItemPortal,
        IDataPortal<AllItemsList> allItemsPortal)
    {
        _itemEditPortal = itemEditPortal;
        _itemInfoListPortal = itemInfoListPortal;
        _completeItemPortal = completeItemPortal;
        _allItemsPortal = allItemsPortal;
    }

    [HttpGet("api/lists/{listId:guid}/items")]
    [ProducesResponseType(typeof(List<TodoItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListItems(Guid listId)
    {
        TodoItemInfoList items;
        try
        {
            // Incomplete only, pre-sorted by the data-portal query (AC 25, 26, 27).
            items = await _itemInfoListPortal.FetchAsync(listId);
        }
        catch (Exception ex) when (UnwrapListNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "List not found." });
        }

        return Ok(items.Select(ToDto).ToList());
    }

    // BE-08 (AC 29): every incomplete item across all of the user's lists, flattened and
    // pre-sorted by the data-portal query, each row labeled with its source list/entity.
    // No 404 path — ownership is enforced in the query, so an empty account just gets [].
    // The literal "all" segment can't collide with the guid-constrained /api/items/{id} routes.
    [HttpGet("api/items/all")]
    [ProducesResponseType(typeof(List<AllItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListAllItems()
    {
        var items = await _allItemsPortal.FetchAsync();

        return Ok(items.Select(ToDto).ToList());
    }

    [HttpPost("api/lists/{listId:guid}/items")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(TodoItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateItem(Guid listId, [FromBody] CreateTodoItemRequest request)
    {
        var item = await _itemEditPortal.CreateAsync(listId);
        item.Title = request.Title ?? "";
        item.Description = request.Description;
        item.Priority = request.Priority;
        item.DueDate = request.DueDate;

        if (!item.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(item.BrokenRulesCollection));
        }

        try
        {
            item = await item.SaveAsync();
        }
        catch (Exception ex) when (UnwrapListNotFound(ex) is not null)
        {
            // Unowned and nonexistent lists are both 404 so list existence never leaks (AC 11).
            return NotFound(new MessageDto { Message = "List not found." });
        }

        // No GET-single-item endpoint exists, so Location points at the list's items.
        return CreatedAtAction(nameof(ListItems), new { listId }, ToDto(item));
    }

    [HttpPut("api/items/{id:guid}")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(typeof(TodoItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ValidationProblemDto), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateItem(Guid id, [FromBody] UpdateTodoItemRequest request)
    {
        TodoItemEdit item;
        try
        {
            item = await _itemEditPortal.FetchAsync(id);
        }
        catch (Exception ex) when (UnwrapItemNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "Item not found." });
        }

        item.Title = request.Title ?? "";
        item.Description = request.Description;
        item.Priority = request.Priority;
        item.DueDate = request.DueDate;

        if (!item.IsValid)
        {
            return UnprocessableEntity(BuildValidationProblem(item.BrokenRulesCollection));
        }

        item = await item.SaveAsync();

        return Ok(ToDto(item));
    }

    [HttpDelete("api/items/{id:guid}")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteItem(Guid id)
    {
        try
        {
            await _itemEditPortal.DeleteAsync(id);
        }
        catch (Exception ex) when (UnwrapItemNotFound(ex) is not null)
        {
            return NotFound(new MessageDto { Message = "Item not found." });
        }

        return NoContent();
    }

    [HttpPatch("api/items/{id:guid}/complete")]
    [ValidateAntiForgeryToken]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(MessageDto), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CompleteItem(Guid id)
    {
        try
        {
            await _completeItemPortal.ExecuteAsync(id);
        }
        catch (Exception ex) when (UnwrapItemNotFound(ex) is not null)
        {
            // Nonexistent, another user's, and already-completed items are indistinguishable
            // (AC 11/25) — completion is one-way and never re-stamps CompletedAt.
            return NotFound(new MessageDto { Message = "Item not found." });
        }

        return NoContent();
    }

    private static TodoItemDto ToDto(TodoItemEdit item) => new()
    {
        Id = item.Id,
        ListId = item.ListId,
        Title = item.Title,
        Description = item.Description,
        Priority = item.Priority,
        DueDate = item.DueDate,
        IsCompleted = item.IsCompleted,
        CompletedAt = item.CompletedAt,
    };

    private static TodoItemDto ToDto(TodoItemInfo item) => new()
    {
        Id = item.Id,
        ListId = item.ListId,
        Title = item.Title,
        Description = item.Description,
        Priority = item.Priority,
        DueDate = item.DueDate,
        IsCompleted = item.IsCompleted,
        CompletedAt = item.CompletedAt,
    };

    private static AllItemDto ToDto(AllItemInfo item) => new()
    {
        Id = item.Id,
        ListId = item.ListId,
        ListName = item.ListName,
        ScopeType = item.ScopeTypeName,
        ScopeName = item.ScopeName,
        Title = item.Title,
        Description = item.Description,
        Priority = item.Priority,
        DueDate = item.DueDate,
    };

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

    private static TodoItemNotFoundException? UnwrapItemNotFound(Exception ex) => ex switch
    {
        TodoItemNotFoundException notFound => notFound,
        DataPortalException { BusinessException: TodoItemNotFoundException notFound } => notFound,
        _ => null,
    };

    private static TodoItemListNotFoundException? UnwrapListNotFound(Exception ex) => ex switch
    {
        TodoItemListNotFoundException notFound => notFound,
        DataPortalException { BusinessException: TodoItemListNotFoundException notFound } => notFound,
        _ => null,
    };
}
