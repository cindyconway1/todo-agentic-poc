using Csla;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// Marks an item complete — strictly one-way (AC 25): sets IsCompleted=true and stamps
/// CompletedAt once. An item the caller doesn't own is rejected as not-found (never 403), and
/// so is an already-completed item — completed items are hidden from every path, so there is no
/// way to re-complete (which would re-stamp CompletedAt) or to un-complete (no reverse path
/// exists anywhere: this command only ever sets true, and TodoItemEdit never writes completion).
/// </summary>
[Serializable]
public class CompleteItemCommand : CommandBase<CompleteItemCommand>
{
    public static readonly PropertyInfo<DateTime?> CompletedAtProperty = RegisterProperty<DateTime?>(nameof(CompletedAt));
    public DateTime? CompletedAt
    {
        get => ReadProperty(CompletedAtProperty);
        private set => LoadProperty(CompletedAtProperty, value);
    }

    [Execute]
    private async Task ExecuteAsync(
        Guid itemId,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        var entity = await dbContext.TodoItems
            .SingleOrDefaultAsync(i => i.Id == itemId
                && i.OwnerUserId == currentUser.CurrentUserId
                && !i.IsCompleted)
            ?? throw new TodoItemNotFoundException(itemId);

        entity.IsCompleted = true;
        entity.CompletedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        CompletedAt = entity.CompletedAt;
    }
}
