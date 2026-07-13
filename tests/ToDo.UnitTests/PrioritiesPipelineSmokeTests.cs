using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToDo.Api.Auth;
using ToDo.DataAccess;

namespace ToDo.UnitTests;

/// <summary>
/// DB-free smoke tests for the priorities route's wiring (BE-10): GET /api/priorities is
/// mapped, sits behind the AuthenticatedUser policy (401 unauthenticated, not a 404 miss or a
/// 302 redirect), and — with the SQL Server DbContext swapped for the in-memory provider whose
/// EnsureCreated applies the HasData seed — the full pipeline returns 200 with the controller,
/// data portal, PriorityList, and PriorityDto all resolving and serializing. No SQL Server, so
/// this runs in the same no-DB gate as the rest of the unit suite.
/// </summary>
public class PrioritiesPipelineSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public PrioritiesPipelineSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly WebApplicationFactoryClientOptions ClientOptions = new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    };

    [Fact]
    public async Task PrioritiesRoute_WhenUnauthenticated_Returns401_NotRedirectOr404()
    {
        var response = await _factory.CreateClient(ClientOptions).GetAsync("/api/priorities");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // The authorized path booted end-to-end without SQL Server: authenticated via a test
    // scheme, DbContext swapped for the seeded in-memory provider — the endpoint returns 200
    // with the three seeded rows serialized { id, name, sortOrder } in sortOrder order.
    [Fact]
    public async Task GetPriorities_Authenticated_Returns200WithSeededRowsInOrder()
    {
        // Named once outside the options lambda: DbContextOptions are scoped, so the lambda
        // runs per request scope and an inline NewGuid would name a fresh store every time.
        var databaseName = "priorities_smoke_" + Guid.NewGuid().ToString("N");
        var factory = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            // EF 8+ composes DbContextOptions from registered configuration actions, so the
            // SqlServer UseSqlServer configuration must go too or both providers end up applied.
            services.RemoveAll<IDbContextOptionsConfiguration<ApplicationDbContext>>();
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(
                o => o.UseInMemoryDatabase(databaseName));
        }));

        using (var scope = factory.Services.CreateScope())
        {
            // EnsureCreated applies the Priorities HasData seed to the in-memory store.
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await ctx.Database.EnsureCreatedAsync();
        }

        var response = await factory.CreateClient(ClientOptions).GetAsync("/api/priorities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetProperty("id").GetInt32()).ToArray());
        Assert.Equal(new[] { "High", "Medium", "Low" }, rows.Select(r => r.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetProperty("sortOrder").GetInt32()).ToArray());
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        private static readonly Guid TestUserId = Guid.Parse("7f1a3c5e-0000-4000-8000-000000000010");

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(AuthClaimTypes.UserId, TestUserId.ToString()),
                new Claim(AuthClaimTypes.Email, "smoke@example.com"),
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
