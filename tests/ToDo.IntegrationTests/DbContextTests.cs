using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ToDo.DataAccess;

namespace ToDo.IntegrationTests;

/// <summary>
/// TDD note: these tests were written before the ApplicationDbContext implementation.
/// Without the StampAuditColumns logic in SaveChangesAsync, tests 2 and 3 fail
/// because CreateDt/LastUpdateDt remain at default (DateTime.MinValue).
/// Test 1 fails if no migration exists or the context cannot connect.
/// </summary>
public class DbContextTests
{
    private static string GetConnectionString(string databaseName)
    {
        var baseConn = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";
        var csb = new SqlConnectionStringBuilder(baseConn);
        csb.InitialCatalog = databaseName;
        return csb.ConnectionString;
    }

    [Fact]
    public async Task Migrations_ApplyToThrowawayDb_AndContextConnects()
    {
        var connStr = GetConnectionString($"IntTest_{Guid.NewGuid():N}");
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connStr)
            .Options;

        await using var ctx = new ApplicationDbContext(options, new FixedCurrentUserAccessor(null));
        try
        {
            await ctx.Database.MigrateAsync();
            Assert.True(await ctx.Database.CanConnectAsync());
        }
        finally
        {
            await ctx.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SaveChangesAsync_OnInsert_StampsCreateDtAndCreateUserId()
    {
        var connStr = GetConnectionString($"IntTest_{Guid.NewGuid():N}");
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connStr)
            .Options;

        await using var ctx = new AuditTestDbContext(options, new FixedCurrentUserAccessor(userId));
        try
        {
            await ctx.Database.EnsureCreatedAsync();

            var before = DateTime.UtcNow.AddSeconds(-1);
            var entity = new AuditTestEntity { Id = Guid.NewGuid(), Value = "test" };
            ctx.AuditTestEntities.Add(entity);
            await ctx.SaveChangesAsync();
            var after = DateTime.UtcNow.AddSeconds(1);

            Assert.InRange(entity.CreateDt, before, after);
            Assert.Equal(userId, entity.CreateUserId);
            Assert.InRange(entity.LastUpdateDt, before, after);
            Assert.Equal(userId, entity.LastUpdateUserId);
        }
        finally
        {
            await ctx.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task SaveChangesAsync_OnUpdate_StampsLastUpdateDtAndLastUpdateUserId()
    {
        var connStr = GetConnectionString($"IntTest_{Guid.NewGuid():N}");
        var insertUserId = Guid.NewGuid();
        var updateUserId = Guid.NewGuid();
        var entityId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connStr)
            .Options;

        DateTime originalCreateDt;
        Guid? originalCreateUserId;

        // Insert phase
        await using (var insertCtx = new AuditTestDbContext(options, new FixedCurrentUserAccessor(insertUserId)))
        {
            await insertCtx.Database.EnsureCreatedAsync();

            var entity = new AuditTestEntity { Id = entityId, Value = "initial" };
            insertCtx.AuditTestEntities.Add(entity);
            await insertCtx.SaveChangesAsync();

            originalCreateDt = entity.CreateDt;
            originalCreateUserId = entity.CreateUserId;
        }

        // Update phase — different user, new context
        await using var updateCtx = new AuditTestDbContext(options, new FixedCurrentUserAccessor(updateUserId));
        try
        {
            var entity = await updateCtx.AuditTestEntities.FindAsync(entityId);
            Assert.NotNull(entity);
            entity.Value = "updated";

            await Task.Delay(5); // ensure clock advances past insert timestamp
            var beforeUpdate = DateTime.UtcNow.AddSeconds(-1);
            await updateCtx.SaveChangesAsync();
            var afterUpdate = DateTime.UtcNow.AddSeconds(1);

            // CreateDt and CreateUserId must be unchanged
            Assert.Equal(originalCreateDt, entity.CreateDt);
            Assert.Equal(originalCreateUserId, entity.CreateUserId);

            // LastUpdate fields must reflect the update actor and time
            Assert.InRange(entity.LastUpdateDt, beforeUpdate, afterUpdate);
            Assert.Equal(updateUserId, entity.LastUpdateUserId);
        }
        finally
        {
            await updateCtx.Database.EnsureDeletedAsync();
        }
    }
}

// ---------------------------------------------------------------------------
// Test helpers — test-only entity and context with an extra DbSet
// ---------------------------------------------------------------------------

internal sealed class AuditTestEntity : AuditableEntity
{
    public Guid Id { get; set; }
    public string Value { get; set; } = "";
}

internal sealed class AuditTestDbContext : ApplicationDbContext
{
    public DbSet<AuditTestEntity> AuditTestEntities => Set<AuditTestEntity>();

    public AuditTestDbContext(DbContextOptions<ApplicationDbContext> options, ICurrentUserAccessor currentUser)
        : base(options, currentUser) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<AuditTestEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Value).HasMaxLength(100);
        });
    }
}

internal sealed class FixedCurrentUserAccessor : ICurrentUserAccessor
{
    public Guid? CurrentUserId { get; }

    public FixedCurrentUserAccessor(Guid? userId) => CurrentUserId = userId;
}
