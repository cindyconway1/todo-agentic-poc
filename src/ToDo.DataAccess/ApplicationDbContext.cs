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
    public DbSet<Volunteer> Volunteers => Set<Volunteer>();
    public DbSet<VolunteerTeam> VolunteerTeams => Set<VolunteerTeam>();
    public DbSet<TodoList> TodoLists => Set<TodoList>();

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

        modelBuilder.Entity<Volunteer>(entity =>
        {
            entity.HasKey(v => v.Id);
            entity.Property(v => v.Name)
                .IsRequired()
                .HasMaxLength(100);
            // Restrict (not cascade), same as Teams: Users→Volunteers plus
            // Users→Leagues→Volunteers(SET NULL) would be multiple cascade paths.
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(v => v.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
            entity.HasIndex(v => v.OwnerUserId);
            // ON DELETE SET NULL: deleting a league clears the tag on any volunteer tagged with it.
            entity.HasOne<League>()
                .WithMany()
                .HasForeignKey(v => v.LeagueId)
                .OnDelete(DeleteBehavior.SetNull)
                .IsRequired(false);
            entity.HasIndex(v => v.LeagueId);
        });

        modelBuilder.Entity<VolunteerTeam>(entity =>
        {
            entity.HasKey(vt => new { vt.VolunteerId, vt.TeamId });
            // Deleting a volunteer removes its tag rows.
            entity.HasOne<Volunteer>()
                .WithMany()
                .HasForeignKey(vt => vt.VolunteerId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            // Deleting a team removes any volunteer-tag rows referencing it (the volunteer survives).
            entity.HasOne<Team>()
                .WithMany()
                .HasForeignKey(vt => vt.TeamId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasIndex(vt => vt.TeamId);
        });

        modelBuilder.Entity<TodoList>(entity =>
        {
            entity.HasKey(l => l.Id);
            // Restrict (not cascade), same as Teams/Volunteers: user deletion is not a feature,
            // and cascading here could form multiple cascade paths with future FKs.
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(l => l.OwnerUserId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
            entity.HasIndex(l => l.OwnerUserId);
            // One implicit list per scope entity: the unique index is what makes the API's
            // get-or-create idempotent under concurrency (the losing insert violates it).
            // ScopeEntityId deliberately has NO foreign key — it references Leagues, Teams, or
            // Volunteers depending on ScopeTypeId, which a single FK cannot express; existence
            // + ownership are enforced at the data portal in TodoListEdit.
            entity.HasIndex(l => new { l.ScopeTypeId, l.ScopeEntityId }).IsUnique();
            // TodoList→TodoItem delete cascade: the FK lives on the TodoItem table, which lands
            // in BE-07 — BE-07 must create TodoItem.ListId with ON DELETE CASCADE.
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
