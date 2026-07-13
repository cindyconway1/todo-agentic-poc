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
/// End-to-end priority-lookup tests (BE-10) against the real ASP.NET Core pipeline + a
/// throwaway SQL Server database (requires SQL Server; runs in CI). Each test class instance
/// gets its own migrated database — which also exercises the string→lookup migration itself.
///
/// AC-mapped: GET /api/priorities returns the three seeded rows in sortOrder order; a valid
/// priorityId set on create/update persists and round-trips both priorityId and priorityName;
/// a non-existent priorityId is a 422 and is NOT silently written; the real FK constraint
/// exists on TodoItems.PriorityId; the per-list and All-Items views come back pre-sorted by
/// the joined Priorities.SortOrder from *real* SQL (not the LINQ-to-objects evaluation the
/// unit tests exercise); and completing a prioritized item hides it from both views.
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
        UserSession session, Guid listId, string title, int? priorityId = null, string? dueDate = null)
    {
        var response = await SendAsync(
            session.Client, HttpMethod.Post, $"/api/lists/{listId}/items", session.Token,
            Json(new { title, priorityId, dueDate }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static int? GetPriorityId(JsonElement item) =>
        item.GetProperty("priorityId").ValueKind == JsonValueKind.Null
            ? null
            : item.GetProperty("priorityId").GetInt32();

    private static string? GetPriorityName(JsonElement item) =>
        item.GetProperty("priorityName").ValueKind == JsonValueKind.Null
            ? null
            : item.GetProperty("priorityName").GetString();

    // AC "GET /api/priorities returns the three seeded rows in order" — from the real migrated
    // table, ordered by SortOrder, in the { id, name, sortOrder } DTO shape.
    [Fact]
    public async Task GetPriorities_ReturnsSeededRowsOrderedBySortOrder()
    {
        var owner = await CreateUserSessionAsync("priorities.lookup@example.com");

        var response = await owner.Client.GetAsync("/api/priorities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var rows = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetProperty("id").GetInt32()).ToArray());
        Assert.Equal(new[] { "High", "Medium", "Low" }, rows.Select(r => r.GetProperty("name").GetString()).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(r => r.GetProperty("sortOrder").GetInt32()).ToArray());
    }

    // AC "the FK constraint exists": the DB-level guarantee behind the business-layer check.
    [Fact]
    public async Task TodoItems_PriorityId_HasRealForeignKeyToPriorities()
    {
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var fkCount = await ctx.Database
            .SqlQueryRaw<int>(
                """
                SELECT COUNT(*) AS [Value]
                FROM sys.foreign_keys fk
                WHERE fk.name = 'FK_TodoItems_Priorities_PriorityId'
                  AND OBJECT_NAME(fk.parent_object_id) = 'TodoItems'
                  AND OBJECT_NAME(fk.referenced_object_id) = 'Priorities'
                """)
            .SingleAsync();

        Assert.Equal(1, fkCount);
    }

    // AC "a valid priorityId persists and round-trips priorityId + priorityName" end-to-end:
    // the create response carries both, and so does a subsequent list read (i.e. it landed in
    // the row, not just the echo). A create without priorityId persists null — not a default.
    [Fact]
    public async Task CreateItem_ViaApi_WithPriorityId_ReturnsIdAndNameInDto()
    {
        var owner = await CreateUserSessionAsync("items.priority.create@example.com");
        var listId = await CreateListAsync(owner, "Priority Create Team");

        var create = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = "Urgent thing", priorityId = 1 }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Guid itemId;
        using (var doc = await ReadJsonAsync(create))
        {
            itemId = doc.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(1, GetPriorityId(doc.RootElement));
            Assert.Equal("High", GetPriorityName(doc.RootElement));
        }

        var noPriority = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = "Whenever thing" }));
        Assert.Equal(HttpStatusCode.Created, noPriority.StatusCode);
        using (var doc = await ReadJsonAsync(noPriority))
        {
            Assert.Null(GetPriorityId(doc.RootElement));
            Assert.Null(GetPriorityName(doc.RootElement));
        }

        var list = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = await ReadJsonAsync(list))
        {
            var items = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            var prioritized = items.Single(i => i.GetProperty("id").GetGuid() == itemId);
            Assert.Equal(1, GetPriorityId(prioritized));
            Assert.Equal("High", GetPriorityName(prioritized));
            var bare = items.Single(i => i.GetProperty("id").GetGuid() != itemId);
            Assert.Null(GetPriorityId(bare));
            Assert.Null(GetPriorityName(bare));
        }
    }

    // AC "priority updates on existing items": 1 (High) → 2 (Medium) via PUT is echoed with
    // both id and name and persisted in the FK column; a follow-up PUT clearing it persists null.
    [Fact]
    public async Task UpdateItem_ViaApi_ChangePriorityId_ReturnsUpdatedDto()
    {
        var owner = await CreateUserSessionAsync("items.priority.update@example.com");
        var listId = await CreateListAsync(owner, "Priority Update Team");
        var itemId = await CreateItemAsync(owner, listId, "Shifting priorities", priorityId: 1);

        var update = await SendAsync(
            owner.Client, HttpMethod.Put, $"/api/items/{itemId}", owner.Token,
            Json(new { title = "Shifting priorities", priorityId = 2 }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using (var doc = await ReadJsonAsync(update))
        {
            Assert.Equal(2, GetPriorityId(doc.RootElement));
            Assert.Equal("Medium", GetPriorityName(doc.RootElement));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
            Assert.Equal(2, row.PriorityId);
        }

        // Clearing the priority persists NULL in the FK column.
        var clear = await SendAsync(
            owner.Client, HttpMethod.Put, $"/api/items/{itemId}", owner.Token,
            Json(new { title = "Shifting priorities", priorityId = (int?)null }));
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        using (var doc = await ReadJsonAsync(clear))
        {
            Assert.Null(GetPriorityId(doc.RootElement));
            Assert.Null(GetPriorityName(doc.RootElement));
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
            Assert.Null(row.PriorityId);
        }
    }

    // AC "a non-existent priorityId is rejected (not silently written)": create returns the
    // contractual 422 shape and no row lands; update returns 422 and the row keeps its old value.
    [Fact]
    public async Task CreateOrUpdateItem_ViaApi_WithUnknownPriorityId_Returns422AndWritesNothing()
    {
        var owner = await CreateUserSessionAsync("items.priority.invalid@example.com");
        var listId = await CreateListAsync(owner, "Priority Invalid Team");

        var create = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = "Bad priority", priorityId = 99 }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        using (var doc = await ReadJsonAsync(create))
        {
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("id").GetString()));
            Assert.Equal("Validation failed.", doc.RootElement.GetProperty("message").GetString());
            var error = Assert.Single(doc.RootElement.GetProperty("errors").EnumerateArray().ToList());
            Assert.Equal("PriorityId", error.GetProperty("property").GetString());
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.Empty(await ctx.TodoItems.Where(i => i.ListId == listId).ToListAsync());
        }

        var itemId = await CreateItemAsync(owner, listId, "Starts valid", priorityId: 3);
        var update = await SendAsync(
            owner.Client, HttpMethod.Put, $"/api/items/{itemId}", owner.Token,
            Json(new { title = "Starts valid", priorityId = 42 }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, update.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
            Assert.Equal(3, row.PriorityId);
        }
    }

    // AC "sort by Priorities.SortOrder, then DueDate, then CreateDt" from real SQL (the joined
    // ORDER BY translation): five items across all priority levels created in an order that
    // differs from the expected output, including a same-priority pair ordered by due date.
    [Fact]
    public async Task ListItems_ViaApi_ReturnsSortedBySortOrder()
    {
        var owner = await CreateUserSessionAsync("items.priority.sort@example.com");
        var listId = await CreateListAsync(owner, "Priority Sort Team");

        await CreateItemAsync(owner, listId, "none", priorityId: null, dueDate: "2026-07-10");
        await CreateItemAsync(owner, listId, "low", priorityId: 3, dueDate: "2026-07-15");
        await CreateItemAsync(owner, listId, "high-late", priorityId: 1, dueDate: "2026-07-25");
        await CreateItemAsync(owner, listId, "medium", priorityId: 2, dueDate: "2026-07-20");
        await CreateItemAsync(owner, listId, "high-early", priorityId: 1, dueDate: "2026-07-20");

        var response = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var titles = doc.RootElement.EnumerateArray()
            .Select(i => i.GetProperty("title").GetString())
            .ToArray();

        Assert.Equal(new[] { "high-early", "high-late", "medium", "low", "none" }, titles);
    }

    // AC "AllItemsList applies the priority-first sort" from real SQL, across multiple lists:
    // the flat view interleaves both lists' items in one §7-ordered sequence, with priorityId
    // and priorityName in each row's DTO.
    [Fact]
    public async Task AllItems_ViaApi_ReturnsSortedBySortOrder()
    {
        var owner = await CreateUserSessionAsync("items.priority.all@example.com");
        var listA = await CreateListAsync(owner, "All Sort Team A");
        var listB = await CreateListAsync(owner, "All Sort Team B");

        await CreateItemAsync(owner, listA, "none-a", priorityId: null, dueDate: "2026-07-10");
        await CreateItemAsync(owner, listB, "high-nodate-b", priorityId: 1);
        await CreateItemAsync(owner, listA, "medium-a", priorityId: 2, dueDate: "2026-07-20");
        await CreateItemAsync(owner, listB, "high-b", priorityId: 1, dueDate: "2026-07-25");
        await CreateItemAsync(owner, listA, "high-a", priorityId: 1, dueDate: "2026-07-20");
        await CreateItemAsync(owner, listB, "low-b", priorityId: 3, dueDate: "2026-07-15");

        var response = await owner.Client.GetAsync("/api/items/all");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var items = doc.RootElement.EnumerateArray().ToList();

        Assert.Equal(
            new[] { "high-a", "high-b", "high-nodate-b", "medium-a", "low-b", "none-a" },
            items.Select(i => i.GetProperty("title").GetString()).ToArray());
        Assert.Equal(
            new int?[] { 1, 1, 1, 2, 3, null },
            items.Select(GetPriorityId).ToArray());
        Assert.Equal(
            new string?[] { "High", "High", "High", "Medium", "Low", null },
            items.Select(GetPriorityName).ToArray());
    }

    // AC "completion is one-way, priority is orthogonal": a High item completes exactly like
    // any other and disappears from both the per-list and All-Items views.
    [Fact]
    public async Task CompleteItem_WithPriorityId_MarksComplete()
    {
        var owner = await CreateUserSessionAsync("items.priority.complete@example.com");
        var listId = await CreateListAsync(owner, "Priority Complete Team");
        var itemId = await CreateItemAsync(owner, listId, "Done despite High", priorityId: 1);
        var survivorId = await CreateItemAsync(owner, listId, "Still open", priorityId: 3);

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

        // The row completed with its priority intact — the FK value survives completion.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
        Assert.True(row.IsCompleted);
        Assert.NotNull(row.CompletedAt);
        Assert.Equal(1, row.PriorityId);
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
