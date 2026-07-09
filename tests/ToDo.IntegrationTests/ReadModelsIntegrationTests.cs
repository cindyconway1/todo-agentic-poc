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
/// End-to-end tests for the BE-08 read models (GET /api/dashboard and GET /api/items/all)
/// against the real ASP.NET Core pipeline + a throwaway SQL Server database (requires SQL
/// Server; runs in CI). Each test class instance gets its own migrated database.
///
/// AC-mapped (BE-08 integration list): dashboard grouping by scope type → entity → lists →
/// sorted incomplete items, including the empty-entity/empty-list decisions (AC 28); completed
/// items excluded from both views (AC 25 carried); due-date sort with nulls last and CreateDt
/// tiebreak asserted in both views (AC 26/27); All-Items flattening across scope types with
/// correct source labels (AC 29); cross-user isolation; and the empty-account shapes.
/// </summary>
public sealed class ReadModelsIntegrationTests : IAsyncLifetime
{
    private readonly ReadModelsApiFactory _factory = new();

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

    private static StringContent Json(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

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

    /// <summary>Creates a league/team/volunteer via its collection route and returns its id.</summary>
    private async Task<Guid> CreateEntityAsync(UserSession session, string collection, string name)
    {
        var response = await SendAsync(
            session.Client, HttpMethod.Post, $"/api/{collection}", session.Token, Json(new { name }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Materializes the entity's implicit list (get-or-create) and returns its id.</summary>
    private async Task<Guid> GetListIdAsync(UserSession session, string scopeTypeName, Guid entityId)
    {
        var response = await session.Client.GetAsync($"/api/lists/{scopeTypeName}/{entityId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateItemAsync(
        UserSession session, Guid listId, string title, string? dueDate = null, string? description = null)
    {
        var response = await SendAsync(
            session.Client, HttpMethod.Post, $"/api/lists/{listId}/items", session.Token,
            Json(new { title, description, dueDate }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task CompleteItemAsync(UserSession session, Guid itemId)
    {
        var response = await SendAsync(
            session.Client, new HttpMethod("PATCH"), $"/api/items/{itemId}/complete", session.Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<JsonDocument> GetDashboardAsync(UserSession session)
    {
        var response = await session.Client.GetAsync("/api/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonDocument> GetAllItemsAsync(UserSession session)
    {
        var response = await session.Client.GetAsync("/api/items/all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static (Guid ListId, string ListName, string[] ItemTitles) ReadSingleList(JsonElement group)
    {
        var list = Assert.Single(group.GetProperty("lists").EnumerateArray().ToList());
        return (
            list.GetProperty("listId").GetGuid(),
            list.GetProperty("listName").GetString()!,
            list.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("title").GetString()!)
                .ToArray());
    }

    // AC 28: lists across a league, a team, and a volunteer land under the correct group and
    // correct entity, each list carrying its incomplete items in sorted order. Also pins the
    // stated decisions: an entity with zero lists does not appear; a list with zero incomplete
    // items still appears with empty items; entities within a group are ordered by name; and
    // listName is the owning entity's name (lists are implicit and have no name of their own).
    [Fact]
    public async Task Dashboard_GroupsListsByScopeTypeAndEntity_WithSortedIncompleteItems()
    {
        var owner = await CreateUserSessionAsync("readmodels.grouping@example.com");

        var leagueId = await CreateEntityAsync(owner, "leagues", "Spring League");
        var leagueListId = await GetListIdAsync(owner, "league", leagueId);
        // Created out of due-date order: the dashboard must return them sorted.
        await CreateItemAsync(owner, leagueListId, "league-second", "2026-08-10");
        await CreateItemAsync(owner, leagueListId, "league-third");
        await CreateItemAsync(owner, leagueListId, "league-first", "2026-08-01");

        // Two teams to pin entity ordering by name; Alpha Team's list stays empty.
        var bravoTeamId = await CreateEntityAsync(owner, "teams", "Bravo Team");
        var bravoListId = await GetListIdAsync(owner, "team", bravoTeamId);
        await CreateItemAsync(owner, bravoListId, "bravo-item");
        var alphaTeamId = await CreateEntityAsync(owner, "teams", "Alpha Team");
        var alphaListId = await GetListIdAsync(owner, "team", alphaTeamId);

        var volunteerId = await CreateEntityAsync(owner, "volunteers", "Casey");
        var volunteerListId = await GetListIdAsync(owner, "volunteer", volunteerId);
        await CreateItemAsync(owner, volunteerListId, "casey-item");

        // An entity whose list was never materialized must not appear at all.
        await CreateEntityAsync(owner, "leagues", "No List League");

        using var dashboard = await GetDashboardAsync(owner);
        var root = dashboard.RootElement;

        // Leagues: only Spring League (No List League has zero lists), items sorted.
        var league = Assert.Single(root.GetProperty("leagues").EnumerateArray().ToList());
        Assert.Equal(leagueId, league.GetProperty("entityId").GetGuid());
        Assert.Equal("Spring League", league.GetProperty("entityName").GetString());
        var (listId, listName, titles) = ReadSingleList(league);
        Assert.Equal(leagueListId, listId);
        Assert.Equal("Spring League", listName);
        Assert.Equal(new[] { "league-first", "league-second", "league-third" }, titles);

        // Teams: ordered by entity name; Alpha Team's empty list still appears with items [].
        var teams = root.GetProperty("teams").EnumerateArray().ToList();
        Assert.Equal(2, teams.Count);
        Assert.Equal(alphaTeamId, teams[0].GetProperty("entityId").GetGuid());
        Assert.Equal("Alpha Team", teams[0].GetProperty("entityName").GetString());
        var (alphaList, _, alphaTitles) = ReadSingleList(teams[0]);
        Assert.Equal(alphaListId, alphaList);
        Assert.Empty(alphaTitles);
        Assert.Equal(bravoTeamId, teams[1].GetProperty("entityId").GetGuid());
        var (_, _, bravoTitles) = ReadSingleList(teams[1]);
        Assert.Equal(new[] { "bravo-item" }, bravoTitles);

        // People == Volunteers.
        var person = Assert.Single(root.GetProperty("people").EnumerateArray().ToList());
        Assert.Equal(volunteerId, person.GetProperty("entityId").GetGuid());
        Assert.Equal("Casey", person.GetProperty("entityName").GetString());
        var (personListId, _, personTitles) = ReadSingleList(person);
        Assert.Equal(volunteerListId, personListId);
        Assert.Equal(new[] { "casey-item" }, personTitles);
    }

    // AC 25 carried into the read models: a completed item appears in neither /api/dashboard
    // nor /api/items/all — while its list still appears on the dashboard with empty items.
    [Fact]
    public async Task CompletedItem_AppearsInNeitherDashboardNorAllItems()
    {
        var owner = await CreateUserSessionAsync("readmodels.completed@example.com");
        var teamId = await CreateEntityAsync(owner, "teams", "Complete Team");
        var listId = await GetListIdAsync(owner, "team", teamId);
        await CreateItemAsync(owner, listId, "keep");
        var doneId = await CreateItemAsync(owner, listId, "done");
        await CompleteItemAsync(owner, doneId);

        using (var dashboard = await GetDashboardAsync(owner))
        {
            var team = Assert.Single(dashboard.RootElement.GetProperty("teams").EnumerateArray().ToList());
            var (_, _, titles) = ReadSingleList(team);
            Assert.Equal(new[] { "keep" }, titles);
        }

        using (var allItems = await GetAllItemsAsync(owner))
        {
            var only = Assert.Single(allItems.RootElement.EnumerateArray().ToList());
            Assert.Equal("keep", only.GetProperty("title").GetString());
        }
    }

    // AC 26/27 carried into the read models: ascending by DueDate, null due dates last, CreateDt
    // ascending as the tiebreak — asserted in both views against real SQL. Sequential inserts
    // give strictly increasing CreateDt stamps, which the tiebreak pairs rely on.
    [Fact]
    public async Task BothViews_SortByDueDateAscending_NullsLast_WithCreateDtTiebreak()
    {
        var owner = await CreateUserSessionAsync("readmodels.sort@example.com");
        var teamId = await CreateEntityAsync(owner, "teams", "Sort Team");
        var listId = await GetListIdAsync(owner, "team", teamId);

        await CreateItemAsync(owner, listId, "no-date-first");                  // null due date, created 1st
        await CreateItemAsync(owner, listId, "late", "2026-09-01");
        await CreateItemAsync(owner, listId, "early-first", "2026-08-01");      // created before its twin
        await CreateItemAsync(owner, listId, "no-date-second");                 // null due date, created 4th
        await CreateItemAsync(owner, listId, "early-second", "2026-08-01");
        await CreateItemAsync(owner, listId, "mid", "2026-08-15");

        var expected = new[] { "early-first", "early-second", "mid", "late", "no-date-first", "no-date-second" };

        using (var dashboard = await GetDashboardAsync(owner))
        {
            var team = Assert.Single(dashboard.RootElement.GetProperty("teams").EnumerateArray().ToList());
            var (_, _, titles) = ReadSingleList(team);
            Assert.Equal(expected, titles);
        }

        using (var allItems = await GetAllItemsAsync(owner))
        {
            var titles = allItems.RootElement.EnumerateArray()
                .Select(i => i.GetProperty("title").GetString())
                .ToArray();
            Assert.Equal(expected, titles);
        }
    }

    // AC 29: items from multiple lists and scope types flatten into one sorted list, each row
    // carrying its correct listName, scopeType, and scopeName (listName == the entity's name,
    // since lists are implicit one-per-entity).
    [Fact]
    public async Task AllItems_FlattensAcrossScopeTypes_WithCorrectSourceLabels()
    {
        var owner = await CreateUserSessionAsync("readmodels.flatten@example.com");

        var leagueId = await CreateEntityAsync(owner, "leagues", "League One");
        var leagueListId = await GetListIdAsync(owner, "league", leagueId);
        await CreateItemAsync(owner, leagueListId, "league-item", "2026-08-02", "from the league");

        var teamId = await CreateEntityAsync(owner, "teams", "Team One");
        var teamListId = await GetListIdAsync(owner, "team", teamId);
        await CreateItemAsync(owner, teamListId, "team-item", "2026-08-01");

        var volunteerId = await CreateEntityAsync(owner, "volunteers", "Vol One");
        var volunteerListId = await GetListIdAsync(owner, "volunteer", volunteerId);
        await CreateItemAsync(owner, volunteerListId, "vol-item");              // no due date → last

        using var allItems = await GetAllItemsAsync(owner);
        var rows = allItems.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, rows.Count);

        Assert.Equal("team-item", rows[0].GetProperty("title").GetString());
        Assert.Equal(teamListId, rows[0].GetProperty("listId").GetGuid());
        Assert.Equal("Team One", rows[0].GetProperty("listName").GetString());
        Assert.Equal("Team", rows[0].GetProperty("scopeType").GetString());
        Assert.Equal("Team One", rows[0].GetProperty("scopeName").GetString());
        Assert.Equal("2026-08-01", rows[0].GetProperty("dueDate").GetString());

        Assert.Equal("league-item", rows[1].GetProperty("title").GetString());
        Assert.Equal(leagueListId, rows[1].GetProperty("listId").GetGuid());
        Assert.Equal("League One", rows[1].GetProperty("listName").GetString());
        Assert.Equal("League", rows[1].GetProperty("scopeType").GetString());
        Assert.Equal("League One", rows[1].GetProperty("scopeName").GetString());
        Assert.Equal("from the league", rows[1].GetProperty("description").GetString());

        Assert.Equal("vol-item", rows[2].GetProperty("title").GetString());
        Assert.Equal(volunteerListId, rows[2].GetProperty("listId").GetGuid());
        Assert.Equal("Vol One", rows[2].GetProperty("listName").GetString());
        Assert.Equal("Volunteer", rows[2].GetProperty("scopeType").GetString());
        Assert.Equal("Vol One", rows[2].GetProperty("scopeName").GetString());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("dueDate").ValueKind);
    }

    // Cross-user isolation: a second user's lists/items/entities never appear in either view —
    // ownership is resolved from the CSLA context, never the request.
    [Fact]
    public async Task ReadModels_NeverLeakAnotherUsersData()
    {
        var userA = await CreateUserSessionAsync("readmodels.isolation.a@example.com");
        var userB = await CreateUserSessionAsync("readmodels.isolation.b@example.com");

        var leagueA = await CreateEntityAsync(userA, "leagues", "A's League");
        var listA = await GetListIdAsync(userA, "league", leagueA);
        await CreateItemAsync(userA, listA, "A's item");

        var teamB = await CreateEntityAsync(userB, "teams", "B's Team");
        var listB = await GetListIdAsync(userB, "team", teamB);
        await CreateItemAsync(userB, listB, "B's item");

        using (var dashboardA = await GetDashboardAsync(userA))
        {
            var root = dashboardA.RootElement;
            var league = Assert.Single(root.GetProperty("leagues").EnumerateArray().ToList());
            Assert.Equal("A's League", league.GetProperty("entityName").GetString());
            Assert.Empty(root.GetProperty("teams").EnumerateArray().ToList());
            Assert.Empty(root.GetProperty("people").EnumerateArray().ToList());
        }

        using (var allItemsA = await GetAllItemsAsync(userA))
        {
            var only = Assert.Single(allItemsA.RootElement.EnumerateArray().ToList());
            Assert.Equal("A's item", only.GetProperty("title").GetString());
        }

        using (var dashboardB = await GetDashboardAsync(userB))
        {
            var root = dashboardB.RootElement;
            Assert.Empty(root.GetProperty("leagues").EnumerateArray().ToList());
            var team = Assert.Single(root.GetProperty("teams").EnumerateArray().ToList());
            Assert.Equal("B's Team", team.GetProperty("entityName").GetString());
        }

        using (var allItemsB = await GetAllItemsAsync(userB))
        {
            var only = Assert.Single(allItemsB.RootElement.EnumerateArray().ToList());
            Assert.Equal("B's item", only.GetProperty("title").GetString());
        }
    }

    // Empty account: a user with no entities/lists gets a well-formed DashboardDto with three
    // empty groups (not nulls, not an error) and an empty All-Items array.
    [Fact]
    public async Task EmptyAccount_GetsWellFormedEmptyDashboard_AndEmptyAllItems()
    {
        var owner = await CreateUserSessionAsync("readmodels.empty@example.com");

        using (var dashboard = await GetDashboardAsync(owner))
        {
            var root = dashboard.RootElement;
            Assert.Equal(JsonValueKind.Array, root.GetProperty("leagues").ValueKind);
            Assert.Equal(JsonValueKind.Array, root.GetProperty("teams").ValueKind);
            Assert.Equal(JsonValueKind.Array, root.GetProperty("people").ValueKind);
            Assert.Empty(root.GetProperty("leagues").EnumerateArray().ToList());
            Assert.Empty(root.GetProperty("teams").EnumerateArray().ToList());
            Assert.Empty(root.GetProperty("people").EnumerateArray().ToList());
        }

        using (var allItems = await GetAllItemsAsync(owner))
        {
            Assert.Equal(JsonValueKind.Array, allItems.RootElement.ValueKind);
            Assert.Empty(allItems.RootElement.EnumerateArray().ToList());
        }
    }
}

/// <summary>WebApplicationFactory that points the app at a per-instance throwaway SQL database.</summary>
internal sealed class ReadModelsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "ReadModelsIntTest_" + Guid.NewGuid().ToString("N");

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
