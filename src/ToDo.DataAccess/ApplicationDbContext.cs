using Microsoft.EntityFrameworkCore;

namespace ToDo.DataAccess;

// Stub: SaveChangesAsync does not yet stamp audit columns.
// Tests will fail until StampAuditColumns is wired up (see next commit).
public class ApplicationDbContext : DbContext
{
    private readonly ICurrentUserAccessor _currentUser;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options, ICurrentUserAccessor currentUser)
        : base(options)
    {
        _currentUser = currentUser;
    }
}
