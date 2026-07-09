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
/// End-to-end leagues tests against the real ASP.NET Core pipeline + a throwaway SQL Server database
/// (requires SQL Server; runs in CI). Each test gets its own migrated database, so tests are
/// independent and self-seeding — no shared fixtures, no ordering dependencies.
///
/// AC-mapped (BE-03 integration list): CRUD round-trip persists and is visible only to the owner
/// (AC 13); owner isolation — cross-user access returns 404 and never leaks existence (AC 11);
/// owner is taken from the authenticated context, not the request body; validation failures -> 422.
/// </summary>
public sealed class LeaguesIntegrationTests : IAsyncLifetime
{
    private readonly LeaguesApiFactory _factory = new();

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

    private sealed record UserSession(HttpClient Client, CookieContainerHandler Cookies, string Token, Guid UserId);

    // Registers + logs in a user and returns a client whose cookie jar holds the auth cookie and a
    // *post-login* antiforgery token (tokens are identity-bound, so the anonymous one won't validate).
    private async Task<UserSession> CreateUserSessionAsync(string email)
    {
        var cookies = new CookieContainerHandler();
        var client = _factory.CreateDefaultClient(new Uri("https://localhost"), cookies);

        var token = await PrimeAntiforgeryAsync(client, cookies);
        var register = await SendAsync(client, HttpMethod.Post, "/api/auth/register", token, CredentialsJson(email));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await SendAsync(client, HttpMethod.Post, "/api/auth/login", token, CredentialsJson(email));
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        token = await PrimeAntiforgeryAsync(client, cookies);

        var me = await client.GetAsync("/api/auth/me");
        me.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        var userId = doc.RootElement.GetProperty("id").GetGuid();

        return new UserSession(client, cookies, token, userId);
    }

    private static StringContent CredentialsJson(string email) =>
        new($"{{\"email\":\"{email}\",\"password\":\"password123\"}}", Encoding.UTF8, "application/json");

    private static StringContent NameJson(string name) =>
        new(JsonSerializer.Serialize(new { name }), Encoding.UTF8, "application/json");

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

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Add("X-XSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<Guid> CreateLeagueAsync(UserSession session, string name)
    {
        var response = await SendAsync(session.Client, HttpMethod.Post, "/api/leagues", session.Token, NameJson(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    // AC 13: create/read/update/delete a league persists and round-trips through the API.
    [Fact]
    public async Task Leagues_CrudRoundTrip_PersistsForOwner()
    {
        var owner = await CreateUserSessionAsync("league.owner@example.com");

        // Create
        var created = await SendAsync(owner.Client, HttpMethod.Post, "/api/leagues", owner.Token, NameJson("Spring League"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid id;
        using (var doc = await ReadJsonAsync(created))
        {
            id = doc.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Spring League", doc.RootElement.GetProperty("name").GetString());
        }

        // Get
        var fetched = await owner.Client.GetAsync($"/api/leagues/{id}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using (var doc = await ReadJsonAsync(fetched))
        {
            Assert.Equal("Spring League", doc.RootElement.GetProperty("name").GetString());
        }

        // List
        var list = await owner.Client.GetAsync("/api/leagues");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = await ReadJsonAsync(list))
        {
            Assert.Contains(doc.RootElement.EnumerateArray(), l => l.GetProperty("id").GetGuid() == id);
        }

        // Update
        var updated = await SendAsync(owner.Client, HttpMethod.Put, $"/api/leagues/{id}", owner.Token, NameJson("Fall League"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using (var doc = await ReadJsonAsync(updated))
        {
            Assert.Equal("Fall League", doc.RootElement.GetProperty("name").GetString());
        }
        var refetched = await owner.Client.GetAsync($"/api/leagues/{id}");
        using (var doc = await ReadJsonAsync(refetched))
        {
            Assert.Equal("Fall League", doc.RootElement.GetProperty("name").GetString());
        }

        // Delete
        var deleted = await SendAsync(owner.Client, HttpMethod.Delete, $"/api/leagues/{id}", owner.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var gone = await owner.Client.GetAsync($"/api/leagues/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // AC 11: another user's league is indistinguishable from a nonexistent one — GET/PUT/DELETE
    // return 404 (never 403) and it never appears in the other user's list.
    [Fact]
    public async Task Leagues_CrossUserAccess_Returns404AndNeverLeaks()
    {
        var userA = await CreateUserSessionAsync("owner.a@example.com");
        var userB = await CreateUserSessionAsync("intruder.b@example.com");
        var id = await CreateLeagueAsync(userA, "A's League");

        var get = await userB.Client.GetAsync($"/api/leagues/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var put = await SendAsync(userB.Client, HttpMethod.Put, $"/api/leagues/{id}", userB.Token, NameJson("Hijacked"));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        var delete = await SendAsync(userB.Client, HttpMethod.Delete, $"/api/leagues/{id}", userB.Token);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var listB = await userB.Client.GetAsync("/api/leagues");
        using (var doc = await ReadJsonAsync(listB))
        {
            Assert.Empty(doc.RootElement.EnumerateArray());
        }

        // A's league is untouched and still visible to A.
        var getA = await userA.Client.GetAsync($"/api/leagues/{id}");
        Assert.Equal(HttpStatusCode.OK, getA.StatusCode);
        using (var doc = await ReadJsonAsync(getA))
        {
            Assert.Equal("A's League", doc.RootElement.GetProperty("name").GetString());
        }
    }

    // Owner comes from the authenticated context: an ownerUserId smuggled into the request body is
    // ignored, and the persisted row belongs to the caller.
    [Fact]
    public async Task CreateLeague_OwnerComesFromContext_NotRequestBody()
    {
        var userA = await CreateUserSessionAsync("context.owner@example.com");
        var userB = await CreateUserSessionAsync("body.owner@example.com");

        var body = new StringContent(
            JsonSerializer.Serialize(new { name = "Context League", ownerUserId = userB.UserId }),
            Encoding.UTF8,
            "application/json");
        var created = await SendAsync(userA.Client, HttpMethod.Post, "/api/leagues", userA.Token, body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid id;
        using (var doc = await ReadJsonAsync(created))
        {
            id = doc.RootElement.GetProperty("id").GetGuid();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var entity = await ctx.Leagues.SingleAsync(l => l.Id == id);
            Assert.Equal(userA.UserId, entity.OwnerUserId);
        }

        var getB = await userB.Client.GetAsync($"/api/leagues/{id}");
        Assert.Equal(HttpStatusCode.NotFound, getB.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task CreateLeague_WithMissingName_Returns422WithValidationShape(string? name)
    {
        var owner = await CreateUserSessionAsync("invalid.create@example.com");

        var body = new StringContent(
            JsonSerializer.Serialize(new { name }),
            Encoding.UTF8,
            "application/json");
        var response = await SendAsync(owner.Client, HttpMethod.Post, "/api/leagues", owner.Token, body);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("message").GetString()));
        Assert.NotEmpty(doc.RootElement.GetProperty("errors").EnumerateArray());
    }

    [Fact]
    public async Task UpdateLeague_WithTooLongName_Returns422()
    {
        var owner = await CreateUserSessionAsync("invalid.update@example.com");
        var id = await CreateLeagueAsync(owner, "Valid Name");

        var response = await SendAsync(owner.Client, HttpMethod.Put, $"/api/leagues/{id}", owner.Token, NameJson(new string('a', 101)));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.NotEmpty(doc.RootElement.GetProperty("errors").EnumerateArray());
    }
}

/// <summary>WebApplicationFactory that points the app at a per-instance throwaway SQL database.</summary>
internal sealed class LeaguesApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "LeaguesIntTest_" + Guid.NewGuid().ToString("N");

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
