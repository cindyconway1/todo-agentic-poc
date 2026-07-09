using Csla;
using Csla.Rules;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.Business;

/// <summary>
/// The single implicit to-do list for one scope entity (League/Team/Volunteer). Users never
/// create, name, or delete lists directly — the API gets-or-creates the list on first access,
/// and the unique (ScopeTypeId, ScopeEntityId) index keeps it 1:1 with its entity. There are
/// deliberately no [Update]/[Delete] operations: a list has no user-editable fields and lives
/// as long as its entity.
/// </summary>
[Serializable]
public class TodoListEdit : BusinessBase<TodoListEdit>
{
    public static readonly PropertyInfo<Guid> IdProperty = RegisterProperty<Guid>(c => c.Id);
    public Guid Id
    {
        get => GetProperty(IdProperty);
        private set => LoadProperty(IdProperty, value);
    }

    // The ScopeType TypeID travels as its integer id (a TypeID instance is not a
    // MobileFormatter-safe managed field); the typed view is the ScopeType property below.
    public static readonly PropertyInfo<int> ScopeTypeIdProperty = RegisterProperty<int>(c => c.ScopeTypeId);
    public int ScopeTypeId
    {
        get => GetProperty(ScopeTypeIdProperty);
        private set => LoadProperty(ScopeTypeIdProperty, value);
    }

    public ScopeType? ScopeType => Business.ScopeType.TryFromId(ScopeTypeId);

    public static readonly PropertyInfo<Guid> ScopeEntityIdProperty = RegisterProperty<Guid>(c => c.ScopeEntityId);
    public Guid ScopeEntityId
    {
        get => GetProperty(ScopeEntityIdProperty);
        private set => LoadProperty(ScopeEntityIdProperty, value);
    }

    protected override void AddBusinessRules()
    {
        base.AddBusinessRules();

        BusinessRules.AddRule(new KnownScopeType(ScopeTypeIdProperty));
        BusinessRules.AddRule(new RequiredGuid(ScopeEntityIdProperty));
    }

    private sealed class KnownScopeType : BusinessRule
    {
        public KnownScopeType(Csla.Core.IPropertyInfo primaryProperty)
            : base(primaryProperty)
        {
            InputProperties.Add(primaryProperty);
        }

        protected override void Execute(IRuleContext context)
        {
            if (context.InputPropertyValues[PrimaryProperty] is not int id || !Business.ScopeType.IsKnownId(id))
            {
                context.AddErrorResult("Scope type must be League, Team, or Volunteer.");
            }
        }
    }

    private sealed class RequiredGuid : BusinessRule
    {
        public RequiredGuid(Csla.Core.IPropertyInfo primaryProperty)
            : base(primaryProperty)
        {
            InputProperties.Add(primaryProperty);
        }

        protected override void Execute(IRuleContext context)
        {
            if (context.InputPropertyValues[PrimaryProperty] is not Guid id || id == Guid.Empty)
            {
                context.AddErrorResult("A scope entity is required.");
            }
        }
    }

    // Polymorphic scope-ownership check (AC 20): ScopeEntityId points at Leagues, Teams, or
    // Volunteers depending on ScopeType, so there is deliberately no DB foreign key on it —
    // existence and ownership of the referenced entity are enforced here at the data portal
    // (defense-in-depth: the controller never sees another user's entities either). Unowned and
    // nonexistent are both a not-found — 404, never 403/422 — so existence never leaks.
    private static async Task EnsureScopeEntityIsOwnedAsync(
        int scopeTypeId, Guid scopeEntityId, Guid ownerUserId, ApplicationDbContext dbContext)
    {
        var scopeType = Business.ScopeType.TryFromId(scopeTypeId)
            ?? throw new ScopeEntityNotFoundException(scopeTypeId, scopeEntityId);

        bool owned;
        if (scopeType == Business.ScopeType.League)
        {
            owned = await dbContext.Leagues.AsNoTracking()
                .AnyAsync(l => l.Id == scopeEntityId && l.OwnerUserId == ownerUserId);
        }
        else if (scopeType == Business.ScopeType.Team)
        {
            owned = await dbContext.Teams.AsNoTracking()
                .AnyAsync(t => t.Id == scopeEntityId && t.OwnerUserId == ownerUserId);
        }
        else
        {
            owned = await dbContext.Volunteers.AsNoTracking()
                .AnyAsync(v => v.Id == scopeEntityId && v.OwnerUserId == ownerUserId);
        }

        if (!owned)
        {
            throw new ScopeEntityNotFoundException(scopeTypeId, scopeEntityId);
        }
    }

    [Create]
    private void Create(int scopeTypeId, Guid scopeEntityId)
    {
        Id = Guid.NewGuid();
        ScopeTypeId = scopeTypeId;
        ScopeEntityId = scopeEntityId;
        BusinessRules.CheckRules();
    }

    [Fetch]
    private async Task FetchAsync(
        int scopeTypeId,
        Guid scopeEntityId,
        [Inject] ApplicationDbContext dbContext,
        [Inject] ICurrentUserAccessor currentUser)
    {
        // Owner-scoped fetch (defense-in-depth): another user's list is a miss, never a 403.
        var entity = await dbContext.TodoLists
            .AsNoTracking()
            .SingleOrDefaultAsync(l => l.ScopeTypeId == scopeTypeId
                && l.ScopeEntityId == scopeEntityId
                && l.OwnerUserId == currentUser.CurrentUserId)
            ?? throw new TodoListNotFoundException(scopeTypeId, scopeEntityId);

        using (BypassPropertyChecks)
        {
            Id = entity.Id;
            ScopeTypeId = entity.ScopeTypeId;
            ScopeEntityId = entity.ScopeEntityId;
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
            ?? throw new InvalidOperationException("An authenticated user is required to create a to-do list.");

        await EnsureScopeEntityIsOwnedAsync(ScopeTypeId, ScopeEntityId, ownerUserId, dbContext);

        var entity = new TodoList
        {
            Id = Id,
            OwnerUserId = ownerUserId,
            ScopeTypeId = ScopeTypeId,
            ScopeEntityId = ScopeEntityId,
        };

        dbContext.TodoLists.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueScopeIndexViolation(ex))
        {
            // Lost the get-or-create race: the unique (ScopeTypeId, ScopeEntityId) index rejected
            // this insert because a concurrent first access already created the implicit list.
            throw new TodoListAlreadyExistsException(ScopeTypeId, ScopeEntityId);
        }
    }

    // SQL Server unique index/constraint violations: 2601 (duplicate key in unique index) and
    // 2627 (unique constraint violation).
    private static bool IsUniqueScopeIndexViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2601 or 2627 };
}
