using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ToDo.Api.Auth;

namespace ToDo.UnitTests;

/// <summary>
/// DB-free smoke tests for the /api/volunteers wiring (BE-05): routing, the AuthenticatedUser
/// policy, and the antiforgery filter on mutations — via WebApplicationFactory, no SQL Server. A 401
/// (not 404) proves the route is mapped and auth challenges cleanly; an authenticated mutation
/// without a token must fail 400 from the antiforgery filter, not 500 from a missing filter service.
/// </summary>
public class VolunteersPipelineSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public VolunteersPipelineSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly WebApplicationFactoryClientOptions ClientOptions = new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    };

    private HttpClient CreateClient() => _factory.CreateClient(ClientOptions);

    // Authenticates every request through a test scheme so filters *behind* auth (antiforgery)
    // are exercised without a database-backed login.
    private WebApplicationFactory<Program> AuthenticatedFactory() => _factory
        .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        }));

    private HttpClient CreateAuthenticatedClient() => AuthenticatedFactory().CreateClient(ClientOptions);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task ListVolunteers_WhenUnauthenticated_Returns401_NotRedirect()
    {
        var response = await CreateClient().GetAsync("/api/volunteers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetVolunteer_WhenUnauthenticated_Returns401()
    {
        var response = await CreateClient().GetAsync($"/api/volunteers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateVolunteer_WhenUnauthenticated_Returns401()
    {
        var response = await CreateClient().PostAsync("/api/volunteers", JsonBody("{\"name\":\"Smoke Volunteer\"}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateVolunteer_WhenUnauthenticated_Returns401()
    {
        var response = await CreateClient().PutAsync($"/api/volunteers/{Guid.NewGuid()}", JsonBody("{\"name\":\"Smoke Volunteer\"}"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteVolunteer_WhenUnauthenticated_Returns401()
    {
        var response = await CreateClient().DeleteAsync($"/api/volunteers/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateVolunteer_Authenticated_WithoutAntiforgeryToken_IsRejectedCleanly()
    {
        var response = await CreateAuthenticatedClient().PostAsync("/api/volunteers", JsonBody("{\"name\":\"Smoke Volunteer\"}"));

        // Must be a clean 400 from the antiforgery filter — NOT a 500 from a missing filter service.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    // Regression anchor: with a non-nullable Name on CreateVolunteerRequest, [ApiController]
    // implicit-required validation would return 400 before VolunteerEdit's Required rule ran, so the
    // contractual 422 path would never be reached. DB-free because VolunteerEdit's [Create] never
    // touches SQL — the request fails validation and returns before any save.
    [Fact]
    public async Task CreateVolunteer_WithNullName_Returns422FromBusinessRule_Not400FromAutoValidation()
    {
        var cookies = new CookieContainerHandler();
        var client = AuthenticatedFactory().CreateDefaultClient(new Uri("https://localhost"), cookies);

        // Prime a real antiforgery token (double-submit: cookie + X-XSRF-TOKEN header) so the
        // request gets past the antiforgery filter and reaches model binding + the business rule.
        var prime = await client.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.NoContent, prime.StatusCode);
        var token = cookies.Container.GetCookies(new Uri("https://localhost"))
            .Cast<Cookie>()
            .Single(c => c.Name == "XSRF-TOKEN")
            .Value;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/volunteers")
        {
            Content = JsonBody("{\"name\":null,\"teamIds\":[]}"),
        };
        request.Headers.Add("X-XSRF-TOKEN", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEmpty(doc.RootElement.GetProperty("errors").EnumerateArray());
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        // Stable across requests: antiforgery tokens are identity-bound, so a per-request random
        // user id would invalidate a token issued on an earlier request by the same client.
        private static readonly Guid TestUserId = Guid.Parse("6f1a3c5e-0000-4000-8000-000000000003");

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
