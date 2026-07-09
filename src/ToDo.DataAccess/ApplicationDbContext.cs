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
    public DbSet<League> Leagues => Set<League>();
    public DbSet<Team> Teams => Set<Team>();

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

        modelBuilder.Entity<League>(entity =>
        {
            entity.HasKey(l => l.Id);
            entity.Property(l => l.Name)
                .IsRequired()
                .HasMaxLength(100);
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(l => l.OwnerUserId)
                .IsRequired();
            entity.HasIndex(l => l.OwnerUserId);
        });

        modelBuilder.Entity<Team>(entity =>
        {
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Name)
                .IsRequired()
                .HasMaxLength(100);
            // Restrict (not cascade): Users→Teams plus Users→Leagues→Teams(SET NULL) would be
            // multiple cascade paths, which SQL Server rejects at CREATE TABLE time.
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(t => t.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
            entity.HasIndex(t => t.OwnerUserId);
            // ON DELETE SET NULL: deleting a league clears the tag on any team tagged with it (AC 18).
            entity.HasOne<League>()
                .WithMany()
                .HasForeignKey(t => t.LeagueId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
            entity.HasIndex(t => t.LeagueId);
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
