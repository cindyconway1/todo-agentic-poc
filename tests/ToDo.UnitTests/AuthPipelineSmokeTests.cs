using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ToDo.UnitTests;

/// <summary>
/// DB-free smoke tests that boot the real ASP.NET Core pipeline via WebApplicationFactory and
/// assert on wiring — DI registration, MVC filters, auth, antiforgery, routing — without SQL Server.
/// These run in the no-DB unit suite that the pre-PR gate self-verifies, so wiring bugs are caught
/// *before* a PR instead of only in CI integration tests.
///
/// Regression anchor: Register_WithoutAntiforgeryToken_IsRejectedCleanly. When Program.cs used
/// AddControllers() instead of AddControllersWithViews(), the [ValidateAntiForgeryToken] filter's
/// backing service was unregistered and every mutation threw HTTP 500. That bug shipped green
/// because nothing here booted the pipeline. This test turns that failure red.
/// </summary>
public class AuthPipelineSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthPipelineSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // https base address so UseHttpsRedirection doesn't 307 and Secure cookies behave; no auto-follow
    // so we assert the exact status the pipeline produced.
    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    });

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task Health_Returns200_AppBoots()
    {
        var response = await CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithoutAntiforgeryToken_IsRejectedCleanly()
    {
        var response = await CreateClient().PostAsync(
            "/api/auth/register",
            JsonBody("{\"email\":\"smoke@example.com\",\"password\":\"password123\"}"));

        // Must be a clean 400 from the antiforgery filter — NOT a 500 from a missing filter service.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithoutAntiforgeryToken_IsRejectedCleanly()
    {
        var response = await CreateClient().PostAsync(
            "/api/auth/login",
            JsonBody("{\"email\":\"smoke@example.com\",\"password\":\"password123\"}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Me_WhenUnauthenticated_Returns401_NotRedirect()
    {
        var response = await CreateClient().GetAsync("/api/auth/me");

        // Cookie auth is configured to return 401 for APIs, never a 302 redirect to a login page.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Antiforgery_ReturnsNoContent_AndIssuesReadableToken()
    {
        var response = await CreateClient().GetAsync("/api/auth/antiforgery");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        Assert.Contains(cookies!, c => c.Contains("XSRF-TOKEN"));
    }
}
