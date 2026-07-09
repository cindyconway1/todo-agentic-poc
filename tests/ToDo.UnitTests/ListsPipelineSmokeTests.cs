using System.Net;
using System.Security.Claims;
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
/// DB-free smoke tests for the /api/lists wiring (BE-06): routing and the AuthenticatedUser
/// policy — via WebApplicationFactory, no SQL Server. A 401 (not 404) proves the route is mapped
/// and auth challenges cleanly. The endpoint is a GET (get-or-create is idempotent, no
/// antiforgery), so there is no mutation/antiforgery path to smoke here; the unknown-scope-type
/// 404 is DB-free because the controller rejects it before any data-portal call.
/// </summary>
public class ListsPipelineSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ListsPipelineSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly WebApplicationFactoryClientOptions ClientOptions = new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    };

    private HttpClient CreateClient() => _factory.CreateClient(ClientOptions);

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

    [Theory]
    [InlineData("league")]
    [InlineData("team")]
    [InlineData("volunteer")]
    public async Task GetOrCreateList_WhenUnauthenticated_Returns401_NotRedirect(string scopeType)
    {
        var response = await CreateClient().GetAsync($"/api/lists/{scopeType}/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetOrCreateList_WithUnknownScopeType_Returns404BeforeAnyDataAccess()
    {
        var response = await CreateAuthenticatedClient().GetAsync($"/api/lists/banana/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        private static readonly Guid TestUserId = Guid.Parse("6f1a3c5e-0000-4000-8000-000000000006");

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
