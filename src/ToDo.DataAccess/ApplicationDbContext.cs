using Microsoft.EntityFrameworkCore;

namespace ToDo.DataAccess;

public class ApplicationDbContext : DbContext
{
    private readonly ICurrentUserAccessor _currentUser;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ICurrentUserAccessor currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditColumns();
        return await base.SaveChangesAsync(cancellationToken);
    }

    private void StampAuditColumns()
    {
        var now = DateTime.UtcNow;
        var userId = _currentUser.CurrentUserId;

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreateDt = now;
                entry.Entity.LastUpdateDt = now;
                entry.Entity.CreateUserId = userId;
                entry.Entity.LastUpdateUserId = userId;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.LastUpdateDt = now;
                entry.Entity.LastUpdateUserId = userId;
            }
        }
    }
}
