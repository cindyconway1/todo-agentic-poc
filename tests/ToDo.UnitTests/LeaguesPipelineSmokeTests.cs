using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToDo.Api.Auth;

namespace ToDo.UnitTests;

/// <summary>
/// DB-free smoke tests for the /api/leagues wiring (BE-03): routing, the AuthenticatedUser policy,
/// and the antiforgery filter on mutations — via WebApplicationFactory, no SQL Server. A 401 (not
/// 404) proves the route is mapped and auth challenges cleanly; an authenticated mutation without a
/// token must fail 400 from the antiforgery filter, not 500 from a missing filter service.
/// </summary>
public class LeaguesPipelineSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public LeaguesPipelineSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly WebApplicationFactoryClientOptions ClientOptions = new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    };

    private HttpClient CreateClient() => _factory.CreateClient(ClientOptions);

    // Authenticates every request through a test scheme so filters *behind* auth (antiforgery)
    // are exercised without a database-backed login.
    private HttpClient CreateAuthenticatedClient() => _factory
        .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        }))
        .CreateClient(ClientOptions);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task ListLeagues_WhenUnauthenticated_Returns401_NotRedirect()
    {
        var response = await CreateClient().GetAsync("/api/leagues");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetLeague_WhenUnauthenticated_Returns401()
    {
        var response = await CreateClient().GetAsync($"/api/leagues/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeague_WhenUnauthenticated_Returns401()
    {
        var response = await CreateClient().PostAsync("/api/leagues", JsonBody("{\"name\":\"Smoke League\"}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateLeague_WhenUnauthenticated_Returns401()
    {
        var response = await CreateClient().PutAsync($"/api/leagues/{Guid.NewGuid()}", JsonBody("{\"name\":\"Smoke League\"}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteLeague_WhenUnauthenticated_Returns401()
    {
        var response = await CreateClient().DeleteAsync($"/api/leagues/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateLeague_Authenticated_WithoutAntiforgeryToken_IsRejectedCleanly()
    {
        var response = await CreateAuthenticatedClient().PostAsync("/api/leagues", JsonBody("{\"name\":\"Smoke League\"}"));

        // Must be a clean 400 from the antiforgery filter — NOT a 500 from a missing filter service.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

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
                new Claim(AuthClaimTypes.UserId, Guid.NewGuid().ToString()),
                new Claim(AuthClaimTypes.Email, "smoke@example.com"),
            };
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
