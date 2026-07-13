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
/// End-to-end priority tests (BE-09) against the real ASP.NET Core pipeline + a throwaway SQL
/// Server database (requires SQL Server; runs in CI). Each test class instance gets its own
/// migrated database.
///
/// AC-mapped: priority set on create is echoed in the DTO and persisted; priority changes on
/// update; the per-list and All-Items views come back pre-sorted by the §7 priority-first order
/// from *real* SQL (the CASE-ranked ORDER BY translation, not the LINQ-to-objects evaluation the
/// unit tests exercise); and completing a prioritized item hides it from both views — completion
/// is one-way and orthogonal to priority.
/// </summary>
public sealed class ItemsPriorityIntegrationTests : IAsyncLifetime
{
    private readonly ItemsPriorityApiFactory _factory = new();

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

    /// <summary>Creates a team for the session's user and returns its implicit list's id.</summary>
    private async Task<Guid> CreateListAsync(UserSession session, string teamName)
    {
        var teamResponse = await SendAsync(
            session.Client, HttpMethod.Post, "/api/teams", session.Token, Json(new { name = teamName }));
        Assert.Equal(HttpStatusCode.Created, teamResponse.StatusCode);
        Guid teamId;
        using (var doc = await ReadJsonAsync(teamResponse))
        {
            teamId = doc.RootElement.GetProperty("id").GetGuid();
        }

        var listResponse = await session.Client.GetAsync($"/api/lists/team/{teamId}");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        using var listDoc = await ReadJsonAsync(listResponse);
        return listDoc.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateItemAsync(
        UserSession session, Guid listId, string title, string? priority = null, string? dueDate = null)
    {
        var response = await SendAsync(
            session.Client, HttpMethod.Post, $"/api/lists/{listId}/items", session.Token,
            Json(new { title, priority, dueDate }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static string? GetPriority(JsonElement item) =>
        item.GetProperty("priority").ValueKind == JsonValueKind.Null
            ? null
            : item.GetProperty("priority").GetString();

    // AC "Priority 'High' persists" end-to-end: the create response carries it, and so does a
    // subsequent list read (i.e. it landed in the row, not just the echo).
    [Fact]
    public async Task CreateItem_ViaApi_WithPriority_ReturnsInDto()
    {
        var owner = await CreateUserSessionAsync("items.priority.create@example.com");
        var listId = await CreateListAsync(owner, "Priority Create Team");

        var create = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = "Urgent thing", priority = "High" }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Guid itemId;
        using (var doc = await ReadJsonAsync(create))
        {
            itemId = doc.RootElement.GetProperty("id").GetGuid();
            Assert.Equal("High", GetPriority(doc.RootElement));
        }

        // A create without priority persists null — not a default.
        var noPriority = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = "Whenever thing" }));
        Assert.Equal(HttpStatusCode.Created, noPriority.StatusCode);
        using (var doc = await ReadJsonAsync(noPriority))
        {
            Assert.Null(GetPriority(doc.RootElement));
        }

        var list = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = await ReadJsonAsync(list))
        {
            var items = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal("High", GetPriority(items.Single(i => i.GetProperty("id").GetGuid() == itemId)));
            Assert.Null(GetPriority(items.Single(i => i.GetProperty("id").GetGuid() != itemId)));
        }
    }

    // AC "Priority updates on existing items": High → Medium via PUT is echoed and persisted;
    // a follow-up PUT clearing it persists null.
    [Fact]
    public async Task UpdateItem_ViaApi_ChangePriority_ReturnsUpdatedDto()
    {
        var owner = await CreateUserSessionAsync("items.priority.update@example.com");
        var listId = await CreateListAsync(owner, "Priority Update Team");
        var itemId = await CreateItemAsync(owner, listId, "Shifting priorities", priority: "High");

        var update = await SendAsync(
            owner.Client, HttpMethod.Put, $"/api/items/{itemId}", owner.Token,
            Json(new { title = "Shifting priorities", priority = "Medium" }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using (var doc = await ReadJsonAsync(update))
        {
            Assert.Equal("Medium", GetPriority(doc.RootElement));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
            Assert.Equal("Medium", row.Priority);
        }

        // Clearing the priority persists NULL in the column.
        var clear = await SendAsync(
            owner.Client, HttpMethod.Put, $"/api/items/{itemId}", owner.Token,
            Json(new { title = "Shifting priorities", priority = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var doc = await ReadJsonAsync(clear))
        {
            Assert.Null(GetPriority(doc.RootElement));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
            Assert.Null(row.Priority);
        }
    }

    // AC "Sort by Priority, then DueDate, then CreateDt" from real SQL: five items across all
    // priority levels created in an order that differs from the expected output, including a
    // same-priority pair that must order by due date.
    [Fact]
    public async Task ListItems_ViaApi_ReturnsSortedByPriority()
    {
        var owner = await CreateUserSessionAsync("items.priority.sort@example.com");
        var listId = await CreateListAsync(owner, "Priority Sort Team");

        await CreateItemAsync(owner, listId, "none", priority: null, dueDate: "2026-07-10");
        await CreateItemAsync(owner, listId, "low", priority: "Low", dueDate: "2026-07-15");
        await CreateItemAsync(owner, listId, "high-late", priority: "High", dueDate: "2026-07-25");
        await CreateItemAsync(owner, listId, "medium", priority: "Medium", dueDate: "2026-07-20");
        await CreateItemAsync(owner, listId, "high-early", priority: "High", dueDate: "2026-07-20");

        var response = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var titles = doc.RootElement.EnumerateArray()
            .Select(i => i.GetProperty("title").GetString())
            .ToArray();

        Assert.Equal(new[] { "high-early", "high-late", "medium", "low", "none" }, titles);
    }

    // AC "AllItemsList applies the priority-first sort" from real SQL, across multiple lists:
    // the flat view interleaves both lists' items in one §7-ordered sequence, with priority in
    // each row's DTO.
    [Fact]
    public async Task AllItems_ViaApi_ReturnsSortedByPriority()
    {
        var owner = await CreateUserSessionAsync("items.priority.all@example.com");
        var listA = await CreateListAsync(owner, "All Sort Team A");
        var listB = await CreateListAsync(owner, "All Sort Team B");

        await CreateItemAsync(owner, listA, "none-a", priority: null, dueDate: "2026-07-10");
        await CreateItemAsync(owner, listB, "high-nodate-b", priority: "High");
        await CreateItemAsync(owner, listA, "medium-a", priority: "Medium", dueDate: "2026-07-20");
        await CreateItemAsync(owner, listB, "high-b", priority: "High", dueDate: "2026-07-25");
        await CreateItemAsync(owner, listA, "high-a", priority: "High", dueDate: "2026-07-20");
        await CreateItemAsync(owner, listB, "low-b", priority: "Low", dueDate: "2026-07-15");

        var response = await owner.Client.GetAsync("/api/items/all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(
            new[] { "high-a", "high-b", "high-nodate-b", "medium-a", "low-b", "none-a" },
            items.Select(i => i.GetProperty("title").GetString()).ToArray());
        Assert.Equal(
            new string?[] { "High", "High", "High", "Medium", "Low", null },
            items.Select(GetPriority).ToArray());
    }

    // AC "completion is one-way, priority is orthogonal": a High item completes exactly like
    // any other and disappears from both the per-list and All-Items views.
    [Fact]
    public async Task CompleteItem_WithPriority_MarksComplete()
    {
        var owner = await CreateUserSessionAsync("items.priority.complete@example.com");
        var listId = await CreateListAsync(owner, "Priority Complete Team");
        var itemId = await CreateItemAsync(owner, listId, "Done despite High", priority: "High");
        var survivorId = await CreateItemAsync(owner, listId, "Still open", priority: "Low");

        var complete = await SendAsync(
            owner.Client, new HttpMethod("PATCH"), $"/api/items/{itemId}/complete", owner.Token);
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        // Gone from the per-list view; the Low item remains.
        var list = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = await ReadJsonAsync(list))
        {
            var only = Assert.Single(doc.RootElement.EnumerateArray().ToList());
            Assert.Equal(survivorId, only.GetProperty("id").GetGuid());
        }

        // Gone from the All-Items view too.
        var all = await owner.Client.GetAsync("/api/items/all");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        using (var doc = await ReadJsonAsync(all))
        {
            var only = Assert.Single(doc.RootElement.EnumerateArray().ToList());
            Assert.Equal(survivorId, only.GetProperty("id").GetGuid());
        }

        // The row completed with its priority intact — priority survives completion in the DB.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
        Assert.True(row.IsCompleted);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal("High", row.Priority);
    }
}

/// <summary>WebApplicationFactory that points the app at a per-instance throwaway SQL database.</summary>
internal sealed class ItemsPriorityApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "ItemsPriorityIntTest_" + Guid.NewGuid().ToString("N");

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
