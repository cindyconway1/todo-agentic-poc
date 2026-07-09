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
/// End-to-end volunteers tests against the real ASP.NET Core pipeline + a throwaway SQL Server
/// database (requires SQL Server; runs in CI). Each test gets its own migrated database, so tests
/// are independent and self-seeding — no shared fixtures, no ordering dependencies.
///
/// AC-mapped (BE-05 integration list): CRUD round-trip persists and is visible only to the owner,
/// and cross-user access returns 404 and never leaks existence (AC 13); tagging with one owned
/// league + multiple owned teams persists and round-trips on read (AC 16); tagging with an unowned
/// or nonexistent league/team is rejected 404 with no tags applied (AC 17); deleting a tagged team
/// removes only the join row and the volunteer survives (the ON DELETE CASCADE join FK); an update
/// that removes a team from the set clears only that join row.
/// </summary>
public sealed class VolunteersIntegrationTests : IAsyncLifetime
{
    private readonly VolunteersApiFactory _factory = new();

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

    private static StringContent VolunteerJson(string? name, Guid? leagueId = null, IEnumerable<Guid>? teamIds = null) =>
        new(JsonSerializer.Serialize(new { name, leagueId, teamIds = teamIds?.ToArray() ?? [] }), Encoding.UTF8, "application/json");

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

    private static async Task<Guid> CreateTeamAsync(UserSession session, string name)
    {
        var response = await SendAsync(session.Client, HttpMethod.Post, "/api/teams", session.Token, NameJson(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateVolunteerAsync(
        UserSession session, string name, Guid? leagueId = null, IEnumerable<Guid>? teamIds = null)
    {
        var response = await SendAsync(session.Client, HttpMethod.Post, "/api/volunteers", session.Token, VolunteerJson(name, leagueId, teamIds));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static Guid? GetLeagueId(JsonElement volunteer) =>
        volunteer.GetProperty("leagueId").ValueKind == JsonValueKind.Null ? null : volunteer.GetProperty("leagueId").GetGuid();

    private static List<Guid> GetTeamIds(JsonElement volunteer) =>
        volunteer.GetProperty("teamIds").EnumerateArray().Select(t => t.GetGuid()).ToList();

    // AC 13: create/read/update/delete a volunteer persists and round-trips through the API.
    [Fact]
    public async Task Volunteers_CrudRoundTrip_PersistsForOwner()
    {
        var owner = await CreateUserSessionAsync("volunteer.owner@example.com");

        // Create
        var created = await SendAsync(owner.Client, HttpMethod.Post, "/api/volunteers", owner.Token, VolunteerJson("Pat Referee"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Guid id;
        using (var doc = await ReadJsonAsync(created))
        {
            id = doc.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("Pat Referee", doc.RootElement.GetProperty("name").GetString());
            Assert.Null(GetLeagueId(doc.RootElement));
            Assert.Empty(GetTeamIds(doc.RootElement));
        }

        // Get
        var fetched = await owner.Client.GetAsync($"/api/volunteers/{id}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using (var doc = await ReadJsonAsync(fetched))
        {
            Assert.Equal("Pat Referee", doc.RootElement.GetProperty("name").GetString());
        }

        // List
        var list = await owner.Client.GetAsync("/api/volunteers");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = await ReadJsonAsync(list))
        {
            Assert.Contains(doc.RootElement.EnumerateArray(), v => v.GetProperty("id").GetGuid() == id);
        }

        // Update
        var updated = await SendAsync(owner.Client, HttpMethod.Put, $"/api/volunteers/{id}", owner.Token, VolunteerJson("Sam Scorekeeper"));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using (var doc = await ReadJsonAsync(updated))
        {
            Assert.Equal("Sam Scorekeeper", doc.RootElement.GetProperty("name").GetString());
        }
        var refetched = await owner.Client.GetAsync($"/api/volunteers/{id}");
        using (var doc = await ReadJsonAsync(refetched))
        {
            Assert.Equal("Sam Scorekeeper", doc.RootElement.GetProperty("name").GetString());
        }

        // Delete
        var deleted = await SendAsync(owner.Client, HttpMethod.Delete, $"/api/volunteers/{id}", owner.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var gone = await owner.Client.GetAsync($"/api/volunteers/{id}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // AC 16: tagging a volunteer with one owned league + multiple owned teams persists and
    // round-trips on read (single fetch and list read model alike).
    [Fact]
    public async Task Volunteers_TagWithLeagueAndTeams_PersistsAndRoundTrips()
    {
        var owner = await CreateUserSessionAsync("volunteer.tagger@example.com");
        var leagueId = await CreateLeagueAsync(owner, "Spring League");
        var teamId1 = await CreateTeamAsync(owner, "Team One");
        var teamId2 = await CreateTeamAsync(owner, "Team Two");

        var volunteerId = await CreateVolunteerAsync(owner, "Tagged Volunteer", leagueId, [teamId1, teamId2]);

        var fetched = await owner.Client.GetAsync($"/api/volunteers/{volunteerId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using (var doc = await ReadJsonAsync(fetched))
        {
            Assert.Equal(leagueId, GetLeagueId(doc.RootElement));
            Assert.Equal(new[] { teamId1, teamId2 }.Order().ToList(), GetTeamIds(doc.RootElement).Order().ToList());
        }

        // The tags are also visible in the list read model.
        var list = await owner.Client.GetAsync("/api/volunteers");
        using (var doc = await ReadJsonAsync(list))
        {
            var volunteer = doc.RootElement.EnumerateArray().Single(v => v.GetProperty("id").GetGuid() == volunteerId);
            Assert.Equal(leagueId, GetLeagueId(volunteer));
            Assert.Equal(new[] { teamId1, teamId2 }.Order().ToList(), GetTeamIds(volunteer).Order().ToList());
        }
    }

    // AC 17: tagging with a league I don't own — or one that doesn't exist — is rejected 404
    // (indistinguishable outcomes, so existence never leaks) and no volunteer/tag is persisted.
    [Fact]
    public async Task CreateVolunteer_WithUnownedOrNonexistentLeague_Returns404AndNoTagApplied()
    {
        var userA = await CreateUserSessionAsync("vol.tagger.a@example.com");
        var userB = await CreateUserSessionAsync("vol.league.b@example.com");
        var foreignLeagueId = await CreateLeagueAsync(userB, "B's League");

        var unowned = await SendAsync(userA.Client, HttpMethod.Post, "/api/volunteers", userA.Token, VolunteerJson("Sneaky Volunteer", foreignLeagueId));
        Assert.Equal(HttpStatusCode.NotFound, unowned.StatusCode);

        var nonexistent = await SendAsync(userA.Client, HttpMethod.Post, "/api/volunteers", userA.Token, VolunteerJson("Ghost Volunteer", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);

        // No volunteer row was persisted by either rejected create.
        var list = await userA.Client.GetAsync("/api/volunteers");
        using var doc = await ReadJsonAsync(list);
        Assert.Empty(doc.RootElement.EnumerateArray());
    }

    // AC 17: tagging with a team I don't own — or one that doesn't exist — is rejected 404 and no
    // tags are applied (the whole create is rejected, even when other tags in the set are owned).
    [Fact]
    public async Task CreateVolunteer_WithUnownedOrNonexistentTeam_Returns404AndNoTagsApplied()
    {
        var userA = await CreateUserSessionAsync("vol.teamtag.a@example.com");
        var userB = await CreateUserSessionAsync("vol.team.b@example.com");
        var ownedTeamId = await CreateTeamAsync(userA, "A's Team");
        var foreignTeamId = await CreateTeamAsync(userB, "B's Team");

        var unowned = await SendAsync(userA.Client, HttpMethod.Post, "/api/volunteers", userA.Token,
            VolunteerJson("Sneaky Volunteer", null, [ownedTeamId, foreignTeamId]));
        Assert.Equal(HttpStatusCode.NotFound, unowned.StatusCode);

        var nonexistent = await SendAsync(userA.Client, HttpMethod.Post, "/api/volunteers", userA.Token,
            VolunteerJson("Ghost Volunteer", null, [Guid.NewGuid()]));
        Assert.Equal(HttpStatusCode.NotFound, nonexistent.StatusCode);

        // No volunteer row was persisted by either rejected create.
        var list = await userA.Client.GetAsync("/api/volunteers");
        using var doc = await ReadJsonAsync(list);
        Assert.Empty(doc.RootElement.EnumerateArray());
    }

    // AC 17 (update path): retagging an existing volunteer with an unowned team is rejected 404 and
    // the volunteer's stored tag set is untouched.
    [Fact]
    public async Task UpdateVolunteer_WithUnownedTeam_Returns404AndTagsUnchanged()
    {
        var userA = await CreateUserSessionAsync("vol.retagger.a@example.com");
        var userB = await CreateUserSessionAsync("vol.team.owner.b@example.com");
        var ownedTeamId = await CreateTeamAsync(userA, "A's Team");
        var foreignTeamId = await CreateTeamAsync(userB, "B's Team");
        var volunteerId = await CreateVolunteerAsync(userA, "Loyal Volunteer", null, [ownedTeamId]);

        var response = await SendAsync(userA.Client, HttpMethod.Put, $"/api/volunteers/{volunteerId}", userA.Token,
            VolunteerJson("Loyal Volunteer", null, [ownedTeamId, foreignTeamId]));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var fetched = await userA.Client.GetAsync($"/api/volunteers/{volunteerId}");
        using var doc = await ReadJsonAsync(fetched);
        Assert.Equal(new List<Guid> { ownedTeamId }, GetTeamIds(doc.RootElement));
    }

    // Join cascade: deleting a team that a volunteer is tagged with removes only the join row —
    // the volunteer survives with the remaining tags (the ON DELETE CASCADE FK at work).
    [Fact]
    public async Task DeleteTeam_VolunteerTaggedWithIt_JoinRowRemovedAndVolunteerSurvives()
    {
        var owner = await CreateUserSessionAsync("vol.cascade.owner@example.com");
        var doomedTeamId = await CreateTeamAsync(owner, "Doomed Team");
        var survivingTeamId = await CreateTeamAsync(owner, "Surviving Team");
        var volunteerId = await CreateVolunteerAsync(owner, "Resilient Volunteer", null, [doomedTeamId, survivingTeamId]);

        var deleted = await SendAsync(owner.Client, HttpMethod.Delete, $"/api/teams/{doomedTeamId}", owner.Token);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        // The volunteer survives with only the doomed team's tag cleared.
        var fetched = await owner.Client.GetAsync($"/api/volunteers/{volunteerId}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        using var doc = await ReadJsonAsync(fetched);
        Assert.Equal("Resilient Volunteer", doc.RootElement.GetProperty("name").GetString());
        Assert.Equal(new List<Guid> { survivingTeamId }, GetTeamIds(doc.RootElement));
    }

    // Reconcile on update: removing one team from the set clears only that join row; the other
    // tags are untouched.
    [Fact]
    public async Task UpdateVolunteer_RemovingATeamFromTheSet_ClearsOnlyThatJoinRow()
    {
        var owner = await CreateUserSessionAsync("vol.reconcile.owner@example.com");
        var teamId1 = await CreateTeamAsync(owner, "Kept Team");
        var teamId2 = await CreateTeamAsync(owner, "Dropped Team");
        var volunteerId = await CreateVolunteerAsync(owner, "Choosy Volunteer", null, [teamId1, teamId2]);

        var updated = await SendAsync(owner.Client, HttpMethod.Put, $"/api/volunteers/{volunteerId}", owner.Token,
            VolunteerJson("Choosy Volunteer", null, [teamId1]));
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using (var doc = await ReadJsonAsync(updated))
        {
            Assert.Equal(new List<Guid> { teamId1 }, GetTeamIds(doc.RootElement));
        }

        var fetched = await owner.Client.GetAsync($"/api/volunteers/{volunteerId}");
        using (var doc = await ReadJsonAsync(fetched))
        {
            Assert.Equal(new List<Guid> { teamId1 }, GetTeamIds(doc.RootElement));
        }

        // The dropped team itself still exists — only the tag row went away.
        var team = await owner.Client.GetAsync($"/api/teams/{teamId2}");
        Assert.Equal(HttpStatusCode.OK, team.StatusCode);
    }

    // AC 13/11: another user's volunteer is indistinguishable from a nonexistent one — GET/PUT/
    // DELETE return 404 (never 403) and it never appears in the other user's list.
    [Fact]
    public async Task Volunteers_CrossUserAccess_Returns404AndNeverLeaks()
    {
        var userA = await CreateUserSessionAsync("vol.owner.a@example.com");
        var userB = await CreateUserSessionAsync("vol.intruder.b@example.com");
        var id = await CreateVolunteerAsync(userA, "A's Volunteer");

        var get = await userB.Client.GetAsync($"/api/volunteers/{id}");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var put = await SendAsync(userB.Client, HttpMethod.Put, $"/api/volunteers/{id}", userB.Token, VolunteerJson("Hijacked"));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);

        var delete = await SendAsync(userB.Client, HttpMethod.Delete, $"/api/volunteers/{id}", userB.Token);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var listB = await userB.Client.GetAsync("/api/volunteers");
        using (var doc = await ReadJsonAsync(listB))
        {
            Assert.Empty(doc.RootElement.EnumerateArray());
        }

        // A's volunteer is untouched and still visible to A.
        var getA = await userA.Client.GetAsync($"/api/volunteers/{id}");
        Assert.Equal(HttpStatusCode.OK, getA.StatusCode);
        using (var doc = await ReadJsonAsync(getA))
        {
            Assert.Equal("A's Volunteer", doc.RootElement.GetProperty("name").GetString());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task CreateVolunteer_WithMissingName_Returns422WithValidationShape(string? name)
    {
        var owner = await CreateUserSessionAsync("invalid.volunteer.create@example.com");

        var response = await SendAsync(owner.Client, HttpMethod.Post, "/api/volunteers", owner.Token, VolunteerJson(name));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("message").GetString()));
        Assert.NotEmpty(doc.RootElement.GetProperty("errors").EnumerateArray());
    }
}

/// <summary>WebApplicationFactory that points the app at a per-instance throwaway SQL database.</summary>
internal sealed class VolunteersApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "VolunteersIntTest_" + Guid.NewGuid().ToString("N");

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
