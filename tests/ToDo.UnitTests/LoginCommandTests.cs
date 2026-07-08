using Csla;
using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.Business;
using ToDo.Business.Services;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

// AC-mapped: "login returns a generic outcome on bad credentials" (BE-02 unit test list).
// Uses the EF Core in-memory provider — acceptable here because the test targets the command's
// success/failure *logic and no-information-leak* behavior, not relational/persistence semantics
// (those belong in the real-SQL integration tests).
public class LoginCommandTests
{
    private const string KnownEmail = "user@example.com";
    private const string KnownPassword = "correct-password";
    private static readonly Guid KnownUserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task<LoginCommand> RunLoginAsync(bool seedKnownUser, string email, string password)
    {
        var services = new ServiceCollection();
        services.AddCsla();
        services.AddSingleton<IPasswordHasher, Argon2IdPasswordHasher>();
        services.AddSingleton<ICurrentUserAccessor>(new TestCurrentUserAccessor(null));
        services.AddDbContext<ApplicationDbContext>(
            o => o.UseInMemoryDatabase("login_" + Guid.NewGuid().ToString("N")),
            ServiceLifetime.Singleton);

        var provider = services.BuildServiceProvider();

        if (seedKnownUser)
        {
            var ctx = provider.GetRequiredService<ApplicationDbContext>();
            var hasher = provider.GetRequiredService<IPasswordHasher>();
            ctx.Users.Add(new User { Id = KnownUserId, Email = KnownEmail, PasswordHash = hasher.Hash(KnownPassword) });
            await ctx.SaveChangesAsync();
        }

        var portal = provider.GetRequiredService<IDataPortal<LoginCommand>>();
        return await portal.ExecuteAsync(email, password);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_ReturnsGenericFailure()
    {
        var result = await RunLoginAsync(seedKnownUser: false, "nobody@example.com", "whatever-password");

        Assert.False(result.Succeeded);
        Assert.Null(result.UserId);
        Assert.Null(result.Email);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsGenericFailure()
    {
        var result = await RunLoginAsync(seedKnownUser: true, KnownEmail, "wrong-password");

        Assert.False(result.Succeeded);
        Assert.Null(result.UserId);
        Assert.Null(result.Email);
    }

    [Fact]
    public async Task Login_UnknownEmailAndWrongPassword_ProduceIdenticalOutcome()
    {
        // The whole point of the generic failure: an attacker cannot distinguish "no such email"
        // from "wrong password" by the response.
        var unknownEmail = await RunLoginAsync(seedKnownUser: true, "nobody@example.com", KnownPassword);
        var wrongPassword = await RunLoginAsync(seedKnownUser: true, KnownEmail, "wrong-password");

        Assert.Equal(unknownEmail.Succeeded, wrongPassword.Succeeded);
        Assert.Equal(unknownEmail.UserId, wrongPassword.UserId);
        Assert.Equal(unknownEmail.Email, wrongPassword.Email);
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_Succeeds()
    {
        var result = await RunLoginAsync(seedKnownUser: true, KnownEmail, KnownPassword);

        Assert.True(result.Succeeded);
        Assert.Equal(KnownUserId, result.UserId);
        Assert.Equal(KnownEmail, result.Email);
    }
}
