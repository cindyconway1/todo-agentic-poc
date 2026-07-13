using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// The incomplete items of one list, pre-sorted for the API (spec §7): Priority first
/// (High → Medium → Low → null), then ascending by DueDate with nulls last, tiebreak CreateDt
/// ascending. Completed items never appear (AC 25, 26, 27).
/// </summary>
[Serializable]
public class TodoItemInfoList : ReadOnlyListBase<TodoItemInfoList, TodoItemInfo>
{
    [Fetch]
    private async Task FetchAsync(
        Guid listId,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser,
        [Inject] IChildDataPortal<TodoItemInfo> itemInfoPortal)
    {
        // The list itself must exist and be owned by the caller; unowned and nonexistent are
        // both a not-found (→ 404) so list existence never leaks. Without this check an empty
        // result would be indistinguishable from an empty *owned* list.
        var listIsOwned = await dbContext.TodoLists
            .AsNoTracking()
            .AnyAsync(l => l.Id == listId && l.OwnerUserId == currentUser.CurrentUserId);
        if (!listIsOwned)
        {
            throw new TodoItemListNotFoundException(listId);
        }

        // Sorted in the query so the API returns pre-sorted data (§7): Priority first via the
        // joined Priorities.SortOrder (the bool key orders items with no priority last), then
        // DueDate ascending with null DueDates last, tiebreak CreateDt ascending. The Include
        // loads the lookup row so TodoItemInfo can surface PriorityName.
        var entities = await dbContext.TodoItems
            .AsNoTracking()
            .Include(i => i.Priority)
            .Where(i => i.ListId == listId
                && i.OwnerUserId == currentUser.CurrentUserId
                && !i.IsCompleted)
            .OrderBy(i => i.Priority == null)
            .ThenBy(i => i.Priority!.SortOrder)
            .ThenBy(i => i.DueDate == null)
            .ThenBy(i => i.DueDate)
            .ThenBy(i => i.CreateDt)
            .ToListAsync();

        using (LoadListMode)
        {
            foreach (var entity in entities)
            {
                Add(itemInfoPortal.FetchChild(entity));
            }
        }
    }

    // Child fetch for the dashboard (BE-08): DashboardInfo has already queried, ownership-
    // filtered, completion-filtered, and sorted the rows in its fixed query set, so this just
    // materializes them.
    [FetchChild]
    private void FetchChild(
        List<TodoItem> entities,
        [Inject] IChildDataPortal<TodoItemInfo> itemInfoPortal)
    {
        using (LoadListMode)
        {
            foreach (var entity in entities)
            {
                Add(itemInfoPortal.FetchChild(entity));
            }
        }
    }
}
