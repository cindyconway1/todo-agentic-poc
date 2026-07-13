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
/// DB-free smoke tests for the item routes' wiring (BE-07): routing, the AuthenticatedUser
/// policy, the antiforgery filter on every mutating verb, and body validation — via
/// WebApplicationFactory, no SQL Server. A 401 (not 404) proves each route is mapped and auth
/// challenges cleanly; an authenticated mutation without a token must fail 400 from the
/// antiforgery filter, not 500 from a missing filter service; a malformed due date is a clean
/// 400 from DateOnly JSON binding (AC 24); a null title reaches TodoItemEdit's Required rule
/// and returns the contractual 422 — all before any data access (TodoItemEdit's [Create] never
/// touches SQL).
/// </summary>
public class ItemsPipelineSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ItemsPipelineSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

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

    public static TheoryData<string, string> AllItemRoutes => new()
    {
        { "GET", $"/api/lists/{Guid.NewGuid()}/items" },
        { "POST", $"/api/lists/{Guid.NewGuid()}/items" },
        { "PUT", $"/api/items/{Guid.NewGuid()}" },
        { "DELETE", $"/api/items/{Guid.NewGuid()}" },
        { "PATCH", $"/api/items/{Guid.NewGuid()}/complete" },
    };

    public static TheoryData<string, string> MutatingItemRoutes => new()
    {
        { "POST", $"/api/lists/{Guid.NewGuid()}/items" },
        { "PUT", $"/api/items/{Guid.NewGuid()}" },
        { "DELETE", $"/api/items/{Guid.NewGuid()}" },
        { "PATCH", $"/api/items/{Guid.NewGuid()}/complete" },
    };

    // Every item route is mapped and challenges as 401 (not a 404 miss, not a 302 redirect).
    [Theory]
    [MemberData(nameof(AllItemRoutes))]
    public async Task ItemRoute_WhenUnauthenticated_Returns401_NotRedirect(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method is "GET" or "DELETE" ? null : JsonBody("{\"title\":\"x\"}"),
        };

        var response = await CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Every mutating verb sits behind the antiforgery filter and rejects a token-less request
    // with a clean 400 — NOT a 500 from a missing filter service.
    [Theory]
    [MemberData(nameof(MutatingItemRoutes))]
    public async Task MutatingItemRoute_Authenticated_WithoutAntiforgeryToken_IsRejectedCleanly(string method, string url)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url)
        {
            Content = method is "DELETE" ? null : JsonBody("{\"title\":\"x\"}"),
        };

        var response = await CreateAuthenticatedClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    private async Task<(HttpClient Client, string Token)> CreateTokenedClientAsync()
    {
        var cookies = new CookieContainerHandler();
        var client = AuthenticatedFactory().CreateDefaultClient(new Uri("https://localhost"), cookies);

        // Prime a real antiforgery token (double-submit: cookie + X-XSRF-TOKEN header) so
        // requests get past the antiforgery filter and reach model binding + business rules.
        var prime = await client.GetAsync("/api/auth/antiforgery");
        Assert.Equal(HttpStatusCode.NoContent, prime.StatusCode);
        var token = cookies.Container.GetCookies(new Uri("https://localhost"))
            .Cast<Cookie>()
            .Single(c => c.Name == "XSRF-TOKEN")
            .Value;
        return (client, token);
    }

    private static HttpRequestMessage TokenedRequest(HttpMethod method, string url, string token, string json)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonBody(json) };
        request.Headers.Add("X-XSRF-TOKEN", token);
        return request;
    }

    // AC 24, DB-free: a malformed/impossible due date fails DateOnly JSON binding → 400 before
    // any data-portal call.
    [Theory]
    [InlineData("2026-02-30")]
    [InlineData("not-a-date")]
    public async Task CreateItem_WithMalformedDueDate_Returns400FromModelBinding(string dueDate)
    {
        var (client, token) = await CreateTokenedClientAsync();

        var response = await client.SendAsync(TokenedRequest(
            HttpMethod.Post,
            $"/api/lists/{Guid.NewGuid()}/items",
            token,
            $"{{\"title\":\"Valid\",\"dueDate\":\"{dueDate}\"}}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // BE-10, DB-free: the request DTOs bind priorityId as a nullable int. A JSON body carrying
    // priorityId reaches TodoItemEdit's rules and 422s on the null title — NOT a 400, which
    // would mean the field failed model binding (TodoItemEdit's [Create] never touches SQL,
    // and the priority existence check only runs at save, which this request never reaches).
    [Fact]
    public async Task CreateItem_WithPriorityId_BindsAndReaches422_Not400()
    {
        var (client, token) = await CreateTokenedClientAsync();

        var response = await client.SendAsync(TokenedRequest(
            HttpMethod.Post,
            $"/api/lists/{Guid.NewGuid()}/items",
            token,
            "{\"title\":null,\"priorityId\":2}"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // BE-10, DB-free: priorityId is an int on the wire now — the legacy string contract value
    // fails JSON binding with a clean 400 on both create and update, before any data access.
    [Theory]
    [InlineData("POST", "/api/lists/{0}/items")]
    [InlineData("PUT", "/api/items/{0}")]
    public async Task ItemMutation_WithNonNumericPriorityId_Returns400FromModelBinding(string method, string urlTemplate)
    {
        var (client, token) = await CreateTokenedClientAsync();

        var response = await client.SendAsync(TokenedRequest(
            new HttpMethod(method),
            string.Format(urlTemplate, Guid.NewGuid()),
            token,
            "{\"title\":\"Valid\",\"priorityId\":\"High\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Regression anchor (mirrors the Teams one): with a non-nullable Title on the request DTO,
    // [ApiController] implicit-required validation would 400 before TodoItemEdit's Required rule
    // ran, so the contractual 422 path would never be reached. DB-free because TodoItemEdit's
    // [Create] never touches SQL — validation fails and returns before any save.
    [Fact]
    public async Task CreateItem_WithNullTitle_Returns422FromBusinessRule_Not400FromAutoValidation()
    {
        var (client, token) = await CreateTokenedClientAsync();

        var response = await client.SendAsync(TokenedRequest(
            HttpMethod.Post,
            $"/api/lists/{Guid.NewGuid()}/items",
            token,
            "{\"title\":null}"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEmpty(doc.RootElement.GetProperty("errors").EnumerateArray());
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "Test";

        private static readonly Guid TestUserId = Guid.Parse("7f1a3c5e-0000-4000-8000-000000000007");

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
