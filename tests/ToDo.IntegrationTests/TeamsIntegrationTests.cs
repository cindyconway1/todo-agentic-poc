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
/// End-to-end teams tests against the real ASP.NET Core pipeline + a throwaway SQL Server database
/// (requires SQL Server; runs in CI). Each test gets its own migrated database, so tests are
/// independent and self-seeding — no shared fixtures, no ordering dependencies.
///
/// AC-mapped (BE-04 integration list): CRUD round-trip persists and is visible only to the owner
/// (AC 13); league tag set/clear round-trips on read (AC 15); tagging with an unowned or
/// nonexistent league is rejected 404 with no tag applied (AC 17); deleting a tagged league clears
/// the team's tag via the ON DELETE SET NULL FK (AC 18); cross-user access returns 404 and never
/// leaks existence (AC 13/11).
/// </summary>
public sealed class TeamsIntegrationTests : IAsyncLifetime
{
    private readonly TeamsApiFactory _factory = new();

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

    private static StringContent TeamJson(string? name, Guid? leagueId = null) =>
        new(JsonSerializer.Serialize(new { name, leagueId }), Encoding.UTF8, "application/json");

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

    private static async Task<Guid> CreateTeamAsync(UserSession session, string name, Guid? leagueId = null)
    {
        var response = await SendAsync(session.Client, HttpMethod.Post, "/api/teams", session.Token, TeamJson(name, leagueId));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static Guid? GetLeagueId(JsonElement team) =>
        team.GetProperty("leagueId").ValueKind == JsonValueKind.Null ? null : team.GetProperty("leagueId").GetGuid();

    // AC 13: create/read/update/delete a team persists and round-trips through the API.
    [Fact]
    public async Task Teams_CrudRoundTrip_PersistsForOwner()
    {
        var owner = await CreateUserSessionAsync("team.owner@example.com");

        // Create
        var created = await SendAsync(owner.Client, HttpMethod.Post, "/api/teams", owner.Token, TeamJson("Red Rockets"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid id;
        using (var doc = await ReadJsonAsync(created))
        {
            id = doc.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Red Rockets", doc.RootElement.GetProperty("name").GetString());
            Assert.Null(GetLeagueId(doc.RootElement));
        }

        // Get
        var fetched = await owner.Client.GetAsync($"/api/teams/{id}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using (var doc = await ReadJsonAsync(fetched))
        {
            Assert.Equal("Red Rockets", doc.RootElement.GetProperty("name").GetString());
        }

        // List
        var list = await owner.Client.GetAsync("/api/teams");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = await ReadJsonAsync(list))
        {
            Assert.Contains(doc.RootElement.EnumerateArray(), t => t.GetProperty("id").GetGuid() == id);
        }

        // Update
        var updated = await SendAsync(owner.Client, HttpMethod.Put, $"/api/teams/{id}", owner.Token, TeamJson("Blue Comets"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using (var doc = await ReadJsonAsync(updated))
        {
            Assert.Equal("Blue Comets", doc.RootElement.GetProperty("name").GetString());
        }
        var refetched = await owner.Client.GetAsync($"/api/teams/{id}");
        using (var doc = await ReadJsonAsync(refetched))
        {
            Assert.Equal("Blue Comets", doc.RootElement.GetProperty("name").GetString());
        }

        // Delete
        var deleted = await SendAsync(owner.Client, HttpMethod.Delete, $"/api/teams/{id}", owner.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var gone = await owner.Client.GetAsync($"/api/teams/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // AC 15: tagging a team with an owned league persists and is reflected on read; clearing the
    // tag round-trips too.
    [Fact]
    public async Task Teams_SetThenClearLeagueTag_RoundTripsOnRead()
    {
        var owner = await CreateUserSessionAsync("tag.owner@example.com");
        var leagueId = await CreateLeagueAsync(owner, "Spring League");

        // Tag at creation.
        var teamId = await CreateTeamAsync(owner, "Tagged Team", leagueId);
        var fetched = await owner.Client.GetAsync($"/api/teams/{teamId}");
        using (var doc = await ReadJsonAsync(fetched))
        {
            Assert.Equal(leagueId, GetLeagueId(doc.RootElement));
        }

        // The tag is also visible in the list read model.
        var list = await owner.Client.GetAsync("/api/teams");
        using (var doc = await ReadJsonAsync(list))
        {
            var team = doc.RootElement.EnumerateArray().Single(t => t.GetProperty("id").GetGuid() == teamId);
            Assert.Equal(leagueId, GetLeagueId(team));
        }

        // Clear the tag.
        var cleared = await SendAsync(owner.Client, HttpMethod.Put, $"/api/teams/{teamId}", owner.Token, TeamJson("Tagged Team", null));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using (var doc = await ReadJsonAsync(cleared))
        {
            Assert.Null(GetLeagueId(doc.RootElement));
        }
        var refetched = await owner.Client.GetAsync($"/api/teams/{teamId}");
        using (var doc = await ReadJsonAsync(refetched))
        {
            Assert.Null(GetLeagueId(doc.RootElement));
        }
    }

    // AC 17: tagging with a league I don't own — or one that doesn't exist — is rejected 404
    // (indistinguishable outcomes, so existence never leaks) and no tag/team is persisted.
    [Fact]
    public async Task CreateTeam_WithUnownedOrNonexistentLeague_Returns404AndNoTagApplied()
    {
        var userA = await CreateUserSessionAsync("tagger.a@example.com");
        var userB = await CreateUserSessionAsync("league.b@example.com");
        var foreignLeagueId = await CreateLeagueAsync(userB, "B's League");

        var unowned = await SendAsync(userA.Client, HttpMethod.Post, "/api/teams", userA.Token, TeamJson("Sneaky Team", foreignLeagueId));
        Assert.Equal(HttpStatusCode.NotFound, unowned.StatusCode);

        var nonexistent = await SendAsync(userA.Client, HttpMethod.Post, "/api/teams", userA.Token, TeamJson("Ghost Team", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);

        // No team row was persisted by either rejected create.
        var list = await userA.Client.GetAsync("/api/teams");
        using var doc = await ReadJsonAsync(list);
        Assert.Empty(doc.RootElement.EnumerateArray());
    }

    // AC 17 (update path): retagging an existing team with an unowned league is rejected 404 and
    // the team's stored tag is untouched.
    [Fact]
    public async Task UpdateTeam_WithUnownedLeague_Returns404AndTagUnchanged()
    {
        var userA = await CreateUserSessionAsync("retagger.a@example.com");
        var userB = await CreateUserSessionAsync("league.owner.b@example.com");
        var ownedLeagueId = await CreateLeagueAsync(userA, "A's League");
        var foreignLeagueId = await CreateLeagueAsync(userB, "B's League");
        var teamId = await CreateTeamAsync(userA, "Loyal Team", ownedLeagueId);

        var response = await SendAsync(userA.Client, HttpMethod.Put, $"/api/teams/{teamId}", userA.Token, TeamJson("Loyal Team", foreignLeagueId));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var fetched = await userA.Client.GetAsync($"/api/teams/{teamId}");
        using var doc = await ReadJsonAsync(fetched);
        Assert.Equal(ownedLeagueId, GetLeagueId(doc.RootElement));
    }

    // AC 18: deleting a league that a team is tagged with deletes the league and clears the team's
    // tag (the ON DELETE SET NULL FK at work).
    [Fact]
    public async Task DeleteLeague_TeamTaggedWithIt_LeagueDeletedAndTagCleared()
    {
        var owner = await CreateUserSessionAsync("cascade.owner@example.com");
        var leagueId = await CreateLeagueAsync(owner, "Doomed League");
        var teamId = await CreateTeamAsync(owner, "Orphaned Team", leagueId);

        var deleted = await SendAsync(owner.Client, HttpMethod.Delete, $"/api/leagues/{leagueId}", owner.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var leagueGone = await owner.Client.GetAsync($"/api/leagues/{leagueId}");
        Assert.Equal(HttpStatusCode.NotFound, leagueGone.StatusCode);

        // The team survives with its tag cleared.
        var fetched = await owner.Client.GetAsync($"/api/teams/{teamId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using var doc = await ReadJsonAsync(fetched);
        Assert.Equal("Orphaned Team", doc.RootElement.GetProperty("name").GetString());
        Assert.Null(GetLeagueId(doc.RootElement));
    }

    // AC 13/11: another user's team is indistinguishable from a nonexistent one — GET/PUT/DELETE
    // return 404 (never 403) and it never appears in the other user's list.
    [Fact]
    public async Task Teams_CrossUserAccess_Returns404AndNeverLeaks()
    {
        var userA = await CreateUserSessionAsync("team.owner.a@example.com");
        var userB = await CreateUserSessionAsync("team.intruder.b@example.com");
        var id = await CreateTeamAsync(userA, "A's Team");

        var get = await userB.Client.GetAsync($"/api/teams/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var put = await SendAsync(userB.Client, HttpMethod.Put, $"/api/teams/{id}", userB.Token, TeamJson("Hijacked"));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        var delete = await SendAsync(userB.Client, HttpMethod.Delete, $"/api/teams/{id}", userB.Token);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var listB = await userB.Client.GetAsync("/api/teams");
        using (var doc = await ReadJsonAsync(listB))
        {
            Assert.Empty(doc.RootElement.EnumerateArray());
        }

        // A's team is untouched and still visible to A.
        var getA = await userA.Client.GetAsync($"/api/teams/{id}");
        Assert.Equal(HttpStatusCode.OK, getA.StatusCode);
        using (var doc = await ReadJsonAsync(getA))
        {
            Assert.Equal("A's Team", doc.RootElement.GetProperty("name").GetString());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task CreateTeam_WithMissingName_Returns422WithValidationShape(string? name)
    {
        var owner = await CreateUserSessionAsync("invalid.team.create@example.com");

        var response = await SendAsync(owner.Client, HttpMethod.Post, "/api/teams", owner.Token, TeamJson(name));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("message").GetString()));
        Assert.NotEmpty(doc.RootElement.GetProperty("errors").EnumerateArray());
    }
}

/// <summary>WebApplicationFactory that points the app at a per-instance throwaway SQL database.</summary>
internal sealed class TeamsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "TeamsIntTest_" + Guid.NewGuid().ToString("N");

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
