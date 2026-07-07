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

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Email)
                .IsRequired()
                .HasMaxLength(256)
                .UseCollation("SQL_Latin1_General_CP1_CI_AS");
            entity.Property(u => u.PasswordHash)
                .IsRequired();
            entity.HasIndex(u => u.Email).IsUnique();
        });
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
                // Self-Id fallback: a self-registering User has no authenticated actor yet,
                // so it stamps its own (app-generated) Id as the creator.
                var actorId = userId ?? (entry.Entity as User)?.Id;
                entry.Entity.CreateDt = now;
                entry.Entity.LastUpdateDt = now;
                entry.Entity.CreateUserId = actorId;
                entry.Entity.LastUpdateUserId = actorId;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.LastUpdateDt = now;
                entry.Entity.LastUpdateUserId = userId;
            }
        }
    }
}
