using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ToDo.DataAccess;

// Allows `dotnet ef migrations` to instantiate the context without the full API DI container.
public sealed class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ToDoDb;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new ApplicationDbContext(options, new NullCurrentUserAccessor());
    }
}

file sealed class NullCurrentUserAccessor : ICurrentUserAccessor
{
    public Guid? CurrentUserId => null;
}
