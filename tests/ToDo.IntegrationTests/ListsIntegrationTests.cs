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
/// End-to-end lists tests against the real ASP.NET Core pipeline + a throwaway SQL Server
/// database (requires SQL Server; runs in CI). Each test gets its own migrated database.
///
/// AC-mapped (BE-06 integration list): get-or-create returns the single implicit list for an
/// owned entity and a second call returns the same list, not a duplicate — the unique
/// (ScopeType, ScopeEntityId) index at work (AC 19); a list scoped to an unowned or nonexistent
/// entity is rejected 404 and nothing persists (AC 20); cross-user access to another user's
/// entity's list is a 404 and never leaks existence (AC 11).
/// </summary>
public sealed class ListsIntegrationTests : IAsyncLifetime
{
    private readonly ListsApiFactory _factory = new();

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

    private static async Task<Guid> CreateEntityAsync(UserSession session, string route, string name)
    {
        var response = await SendAsync(session.Client, HttpMethod.Post, route, session.Token, NameJson(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    // AC 19: the implicit list for an owned entity is created on first access and returned with
    // the correct scope; a second call returns the *same* list, not a duplicate — the unique
    // (ScopeType, ScopeEntityId) index makes get-or-create idempotent. Exercised for all three
    // scope types.
    [Theory]
    [InlineData("/api/leagues", "league", "League")]
    [InlineData("/api/teams", "team", "Team")]
    [InlineData("/api/volunteers", "volunteer", "Volunteer")]
    public async Task GetOrCreateList_ForOwnedEntity_CreatesOnceAndIsIdempotent(
        string entityRoute, string scopeTypeRoute, string expectedScopeTypeName)
    {
        var owner = await CreateUserSessionAsync($"list.owner.{scopeTypeRoute}@example.com");
        var entityId = await CreateEntityAsync(owner, entityRoute, $"Scoped {expectedScopeTypeName}");

        // First access auto-creates the list.
        var first = await owner.Client.GetAsync($"/api/lists/{scopeTypeRoute}/{entityId}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Guid listId;
        using (var doc = await ReadJsonAsync(first))
        {
            listId = doc.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(expectedScopeTypeName, doc.RootElement.GetProperty("scopeType").GetString());
            Assert.Equal(entityId, doc.RootElement.GetProperty("scopeEntityId").GetGuid());
        }

        // Second access returns the same list — no duplicate row.
        var second = await owner.Client.GetAsync($"/api/lists/{scopeTypeRoute}/{entityId}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using (var doc = await ReadJsonAsync(second))
        {
            Assert.Equal(listId, doc.RootElement.GetProperty("id").GetGuid());
        }

        // Straight to the database: exactly one list row exists for the scope.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await ctx.TodoLists.Where(l => l.ScopeEntityId == entityId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(listId, row.Id);
        Assert.Equal(owner.UserId, row.OwnerUserId);
    }

    // AC 20: a list scoped to an entity I don't own — or one that doesn't exist, or an unknown
    // scope type — is rejected 404 (indistinguishable outcomes, so existence never leaks) and no
    // list row is persisted.
    [Fact]
    public async Task GetOrCreateList_ForUnownedOrNonexistentEntity_Returns404AndNothingPersists()
    {
        var userA = await CreateUserSessionAsync("list.scope.a@example.com");
        var userB = await CreateUserSessionAsync("list.scope.b@example.com");
        var foreignTeamId = await CreateEntityAsync(userB, "/api/teams", "B's Team");

        var unowned = await userA.Client.GetAsync($"/api/lists/team/{foreignTeamId}");
        Assert.Equal(HttpStatusCode.NotFound, unowned.StatusCode);

        var nonexistent = await userA.Client.GetAsync($"/api/lists/team/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);

        var unknownScopeType = await userA.Client.GetAsync($"/api/lists/banana/{foreignTeamId}");
        Assert.Equal(HttpStatusCode.NotFound, unknownScopeType.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.TodoLists.ToListAsync());
    }

    // AC 11: after user A materializes the list for their entity, user B requesting the same
    // scope still gets 404 (B doesn't own the entity), A's list is untouched, and no second row
    // appears.
    [Fact]
    public async Task GetOrCreateList_CrossUser_Returns404AndNeverLeaksOrDuplicates()
    {
        var userA = await CreateUserSessionAsync("list.isolation.a@example.com");
        var userB = await CreateUserSessionAsync("list.isolation.b@example.com");
        var leagueId = await CreateEntityAsync(userA, "/api/leagues", "A's League");

        var created = await userA.Client.GetAsync($"/api/lists/league/{leagueId}");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Guid listId;
        using (var doc = await ReadJsonAsync(created))
        {
            listId = doc.RootElement.GetProperty("id").GetGuid();
        }

        var intruder = await userB.Client.GetAsync($"/api/lists/league/{leagueId}");
        Assert.Equal(HttpStatusCode.NotFound, intruder.StatusCode);

        // A's list is untouched and still the only row for the scope.
        var again = await userA.Client.GetAsync($"/api/lists/league/{leagueId}");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using (var doc = await ReadJsonAsync(again))
        {
            Assert.Equal(listId, doc.RootElement.GetProperty("id").GetGuid());
        }

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await ctx.TodoLists.Where(l => l.ScopeEntityId == leagueId).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(userA.UserId, row.OwnerUserId);
    }

    // The unique (ScopeType, ScopeEntityId) index itself: inserting a second list row for the
    // same scope at the database level is rejected — proof the 1:1 invariant is enforced by the
    // schema, not just by application logic.
    [Fact]
    public async Task TodoLists_UniqueScopeIndex_RejectsASecondRowForTheSameScope()
    {
        var owner = await CreateUserSessionAsync("list.index.owner@example.com");
        var teamId = await CreateEntityAsync(owner, "/api/teams", "Indexed Team");

        var first = await owner.Client.GetAsync($"/api/lists/team/{teamId}");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ctx.TodoLists.Add(new TodoList
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner.UserId,
            ScopeTypeId = 2, // Team
            ScopeEntityId = teamId,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
        Assert.IsType<SqlException>(ex.InnerException);
    }
}

/// <summary>WebApplicationFactory that points the app at a per-instance throwaway SQL database.</summary>
internal sealed class ListsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "ListsIntTest_" + Guid.NewGuid().ToString("N");

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
