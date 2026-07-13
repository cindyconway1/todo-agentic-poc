using Csla;
using Csla.Rules.CommonRules;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// An editable to-do item. Completion is deliberately not editable here: IsCompleted and
/// CompletedAt are read-only and never written by [Update], so the only way to complete an item
/// is CompleteItemCommand — and because [Fetch]/[Update]/[Delete] all filter IsCompleted == false,
/// a completed item can never be re-fetched, edited, deleted, or un-completed (AC 25).
/// </summary>
[Serializable]
public class TodoItemEdit : BusinessBase<TodoItemEdit>
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 200;

    public static readonly PropertyInfo<Guid> IdProperty = RegisterProperty<Guid>(c => c.Id);
    public Guid Id
    {
        get => GetProperty(IdProperty);
        private set => LoadProperty(IdProperty, value);
    }

    public static readonly PropertyInfo<Guid> ListIdProperty = RegisterProperty<Guid>(c => c.ListId);
    public Guid ListId
    {
        get => GetProperty(ListIdProperty);
        private set => LoadProperty(ListIdProperty, value);
    }

    public static readonly PropertyInfo<string> TitleProperty = RegisterProperty<string>(c => c.Title, "Title", "");
    public string Title
    {
        get => GetProperty(TitleProperty) ?? "";
        // Trimmed at the setter so the Required and MaxLength rules judge the trimmed value
        // ("required + trimmed, 1–200") and the data portal persists it as-is.
        set => SetProperty(TitleProperty, value?.Trim() ?? "");
    }

    public static readonly PropertyInfo<string?> DescriptionProperty = RegisterProperty<string?>(c => c.Description);
    public string? Description
    {
        // CSLA coerces a null string managed field to "" — normalize back so an absent
        // description round-trips (and persists) as null, per the spec's nullable column.
        get => string.IsNullOrEmpty(GetProperty(DescriptionProperty)) ? null : GetProperty(DescriptionProperty);
        set => SetProperty(DescriptionProperty, value);
    }

    public static readonly PropertyInfo<int?> PriorityIdProperty = RegisterProperty<int?>(c => c.PriorityId);
    public int? PriorityId
    {
        // Null is a valid state (no priority). Existence in the Priorities lookup is enforced
        // in [Insert]/[Update] (it needs the database), backed by the FK — an unknown id
        // surfaces as InvalidPriorityException, never a silent write.
        get => GetProperty(PriorityIdProperty);
        set => SetProperty(PriorityIdProperty, value);
    }

    public static readonly PropertyInfo<string?> PriorityNameProperty = RegisterProperty<string?>(c => c.PriorityName);
    public string? PriorityName
    {
        // Read-only projection of the lookup row's Name, loaded by [Fetch]/[Insert]/[Update]
        // so the API can return both priorityId and priorityName without a second query.
        // CSLA coerces a null string managed field to "" — normalize back to null.
        get => string.IsNullOrEmpty(GetProperty(PriorityNameProperty)) ? null : GetProperty(PriorityNameProperty);
        private set => LoadProperty(PriorityNameProperty, value);
    }

    public static readonly PropertyInfo<DateOnly?> DueDateProperty = RegisterProperty<DateOnly?>(c => c.DueDate);
    public DateOnly? DueDate
    {
        get => GetProperty(DueDateProperty);
        set => SetProperty(DueDateProperty, value);
    }

    public static readonly PropertyInfo<bool> IsCompletedProperty = RegisterProperty<bool>(c => c.IsCompleted);
    public bool IsCompleted
    {
        get => GetProperty(IsCompletedProperty);
        private set => LoadProperty(IsCompletedProperty, value);
    }

    public static readonly PropertyInfo<DateTime?> CompletedAtProperty = RegisterProperty<DateTime?>(c => c.CompletedAt);
    public DateTime? CompletedAt
    {
        get => GetProperty(CompletedAtProperty);
        private set => LoadProperty(CompletedAtProperty, value);
    }

    protected override void AddBusinessRules()
    {
        base.AddBusinessRules();

        BusinessRules.AddRule(new Required(TitleProperty));
        BusinessRules.AddRule(new MaxLength(TitleProperty, MaxTitleLength));
        BusinessRules.AddRule(new MaxLength(DescriptionProperty, MaxDescriptionLength));
        // DueDate needs no rule: DateOnly? is valid-by-construction — a malformed or impossible
        // date (e.g. 2026-02-30) is rejected at JSON model binding as a 400 (AC 24).
    }

    [Create]
    private void Create(Guid listId)
    {
        Id = Guid.NewGuid();
        ListId = listId;
        BusinessRules.CheckRules();
    }

    [Fetch]
    private async Task FetchAsync(
        Guid id,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Owner-scoped and incomplete-only (defense-in-depth): another user's item and a
        // completed item are both a miss, never a 403 — completed items appear in no view.
        var entity = await dbContext.TodoItems
            .AsNoTracking()
            .Include(i => i.Priority)
            .SingleOrDefaultAsync(i => i.Id == id
                && i.OwnerUserId == currentUser.CurrentUserId
                && !i.IsCompleted)
            ?? throw new TodoItemNotFoundException(id);

        using (BypassPropertyChecks)
        {
            Id = entity.Id;
            ListId = entity.ListId;
            Title = entity.Title;
            Description = entity.Description;
            PriorityId = entity.PriorityId;
            PriorityName = entity.Priority?.Name;
            DueDate = entity.DueDate;
            IsCompleted = entity.IsCompleted;
            CompletedAt = entity.CompletedAt;
        }

        BusinessRules.CheckRules();
    }

    [Insert]
    private async Task InsertAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Owner comes from the authenticated context, never from client input.
        var ownerUserId = currentUser.CurrentUserId
            ?? throw new InvalidOperationException("An authenticated user is required to create a to-do item.");

        // The target list must exist and be owned by the current user; unowned and nonexistent
        // are both a not-found so list existence never leaks (→ 404 at the controller).
        var listIsOwned = await dbContext.TodoLists
            .AsNoTracking()
            .AnyAsync(l => l.Id == ListId && l.OwnerUserId == ownerUserId);
        if (!listIsOwned)
        {
            throw new TodoItemListNotFoundException(ListId);
        }

        var priority = await ResolvePriorityAsync(dbContext);

        var entity = new TodoItem
        {
            Id = Id,
            ListId = ListId,
            OwnerUserId = ownerUserId,
            Title = Title,
            Description = Description,
            PriorityId = PriorityId,
            DueDate = DueDate,
            IsCompleted = false,
            CompletedAt = null,
        };

        dbContext.TodoItems.Add(entity);
        await dbContext.SaveChangesAsync();

        PriorityName = priority?.Name;
    }

    [Update]
    private async Task UpdateAsync(
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        var entity = await dbContext.TodoItems
            .SingleOrDefaultAsync(i => i.Id == Id
                && i.OwnerUserId == currentUser.CurrentUserId
                && !i.IsCompleted)
            ?? throw new TodoItemNotFoundException(Id);

        var priority = await ResolvePriorityAsync(dbContext);

        // ListId, IsCompleted, and CompletedAt are deliberately not written: items don't move
        // between lists, and completion state only ever changes through CompleteItemCommand.
        entity.Title = Title;
        entity.Description = Description;
        entity.PriorityId = PriorityId;
        entity.DueDate = DueDate;
        await dbContext.SaveChangesAsync();

        PriorityName = priority?.Name;
    }

    // Business-layer defense-in-depth ahead of the DB FK: a non-null PriorityId must exist in
    // the Priorities lookup (→ 422 at the API), and the resolved row supplies PriorityName.
    private async Task<Priority?> ResolvePriorityAsync(ApplicationDbContext dbContext)
    {
        if (PriorityId is not int priorityId)
        {
            return null;
        }

        return await dbContext.Priorities
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == priorityId)
            ?? throw new InvalidPriorityException(priorityId);
    }

    [Delete]
    private async Task DeleteAsync(
        Guid id,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Completed items are hidden from every path, deletion included (AC 25).
        var entity = await dbContext.TodoItems
            .SingleOrDefaultAsync(i => i.Id == id
                && i.OwnerUserId == currentUser.CurrentUserId
                && !i.IsCompleted)
            ?? throw new TodoItemNotFoundException(id);

        dbContext.TodoItems.Remove(entity);
        await dbContext.SaveChangesAsync();
    }
}
