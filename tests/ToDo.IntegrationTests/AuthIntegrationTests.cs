using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ToDo.DataAccess;

namespace ToDo.IntegrationTests;

/// <summary>
/// End-to-end auth tests against the real ASP.NET Core pipeline + a throwaway SQL Server database
/// (requires SQL Server; runs in CI). Each test gets its own migrated database, so tests are
/// independent and self-seeding — no shared fixtures, no ordering dependencies.
///
/// AC-mapped (BE-02 integration list): register success, duplicate email -> 409, login cookie
/// flags (HttpOnly/Secure/SameSite=Strict), me -> 401 unauthenticated, antiforgery rejects a
/// mutation with a missing token.
/// </summary>
public sealed class AuthIntegrationTests : IAsyncLifetime
{
    private readonly AuthApiFactory _factory = new();

    public async Task InitializeAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await ctx.Database.EnsureDeletedAsync();
        }
        await _factory.DisposeAsync();
    }

    private HttpClient CreateClient(out CookieContainerHandler cookies)
    {
        cookies = new CookieContainerHandler();
        return _factory.CreateDefaultClient(new Uri("https://localhost"), cookies);
    }

    private static StringContent Json(string email, string password) =>
        new($"{{\"email\":\"{email}\",\"password\":\"{password}\"}}", Encoding.UTF8, "application/json");

    // Antiforgery double-submit: GET the token (also drops the antiforgery cookie into the jar),
    // then echo the readable XSRF-TOKEN cookie value in the X-XSRF-TOKEN header.
    private static async Task<string> PrimeAntiforgeryAsync(HttpClient client, CookieContainerHandler cookies)
    {
        var response = await client.GetAsync("/api/auth/antiforgery");
        response.EnsureSuccessStatusCode();
        var token = cookies.Container.GetCookies(new Uri("https://localhost"))
            .Cast<Cookie>()
            .FirstOrDefault(c => c.Name == "XSRF-TOKEN");
        Assert.NotNull(token);
        return token!.Value;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string token, HttpContent content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("X-XSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Register_WithValidCredentials_Returns201WithUser()
    {
        var client = CreateClient(out var cookies);
        var token = await PrimeAntiforgeryAsync(client, cookies);

        var response = await PostAsync(client, "/api/auth/register", token, Json("new.user@example.com", "password123"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("new.user@example.com", doc.RootElement.GetProperty("email").GetString());
        Assert.True(Guid.TryParse(doc.RootElement.GetProperty("id").GetString(), out _));
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_Returns409()
    {
        var client = CreateClient(out var cookies);
        var token = await PrimeAntiforgeryAsync(client, cookies);

        var first = await PostAsync(client, "/api/auth/register", token, Json("dupe@example.com", "password123"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostAsync(client, "/api/auth/register", token, Json("dupe@example.com", "password123"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Login_WithValidCredentials_SetsHardenedAuthCookie()
    {
        var client = CreateClient(out var cookies);
        var token = await PrimeAntiforgeryAsync(client, cookies);
        await PostAsync(client, "/api/auth/register", token, Json("login.flags@example.com", "password123"));

        var login = await PostAsync(client, "/api/auth/login", token, Json("login.flags@example.com", "password123"));

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.True(login.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var authCookie = setCookies!.FirstOrDefault(c => c.Contains(".AspNetCore.Cookies"));
        Assert.NotNull(authCookie);
        Assert.Contains("httponly", authCookie!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", authCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Me_WhenUnauthenticated_Returns401()
    {
        var client = CreateClient(out _);

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithoutAntiforgeryToken_Returns400()
    {
        var client = CreateClient(out _);

        var response = await client.PostAsync("/api/auth/register", Json("no.token@example.com", "password123"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}

/// <summary>WebApplicationFactory that points the app at a per-instance throwaway SQL database.</summary>
internal sealed class AuthApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "AuthIntTest_" + Guid.NewGuid().ToString("N");

    private static string BaseConnectionString =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? "Server=(localdb)\\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True";

    private string ConnectionString =>
        new SqlConnectionStringBuilder(BaseConnectionString) { InitialCatalog = _databaseName }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
    }
}
