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
/// End-to-end item tests against the real ASP.NET Core pipeline + a throwaway SQL Server
/// database (requires SQL Server; runs in CI). Each test class instance gets its own migrated
/// database.
///
/// AC-mapped (BE-07 integration list): CRUD round-trip; completion hides the item everywhere and
/// cannot be undone or re-applied (AC 25); sort order ascending by DueDate with nulls last and
/// CreateDt tiebreak, produced by the data-portal query against real SQL (AC 26, 27); a
/// malformed date is a 400 from binding (AC 24); a missing title and an over-long description
/// are 422 business-rule failures (AC 21, 22 — the repo's contractual validation status per
/// .claude/rules/api.md); an item created on a list the user doesn't own is a 404 with nothing
/// persisted, and cross-user access to another user's items is always a 404 (AC 11).
/// </summary>
public sealed class ItemsIntegrationTests : IAsyncLifetime
{
    private readonly ItemsApiFactory _factory = new();

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
        UserSession session, Guid listId, string title, string? dueDate = null, string? description = null)
    {
        var response = await SendAsync(
            session.Client, HttpMethod.Post, $"/api/lists/{listId}/items", session.Token,
            Json(new { title, description, dueDate }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    // CRUD round-trip: create echoes the item (owner stamped server-side), list shows it, update
    // changes fields, delete removes it and it stops appearing.
    [Fact]
    public async Task Items_CrudRoundTrip_CreateListUpdateDelete()
    {
        var owner = await CreateUserSessionAsync("items.crud@example.com");
        var listId = await CreateListAsync(owner, "CRUD Team");

        // Create — the DTO echoes back the fields; title arrives trimmed.
        var create = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = "  Buy oranges  ", description = "small ones", dueDate = "2026-08-01" }));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Guid itemId;
        using (var doc = await ReadJsonAsync(create))
        {
            var root = doc.RootElement;
            itemId = root.GetProperty("id").GetGuid();
            Assert.Equal(listId, root.GetProperty("listId").GetGuid());
            Assert.Equal("Buy oranges", root.GetProperty("title").GetString());
            Assert.Equal("small ones", root.GetProperty("description").GetString());
            Assert.Equal("2026-08-01", root.GetProperty("dueDate").GetString());
            Assert.False(root.GetProperty("isCompleted").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("completedAt").ValueKind);
        }

        // Owner is stamped from the session, never from the body.
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
            Assert.Equal(owner.UserId, row.OwnerUserId);
        }

        // List shows the item.
        var list = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = await ReadJsonAsync(list))
        {
            var only = Assert.Single(doc.RootElement.EnumerateArray().ToList());
            Assert.Equal(itemId, only.GetProperty("id").GetGuid());
        }

        // Update changes fields (and can clear the due date).
        var update = await SendAsync(
            owner.Client, HttpMethod.Put, $"/api/items/{itemId}", owner.Token,
            Json(new { title = "Buy apples", description = (string?)null, dueDate = (string?)null }));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using (var doc = await ReadJsonAsync(update))
        {
            Assert.Equal("Buy apples", doc.RootElement.GetProperty("title").GetString());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("description").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("dueDate").ValueKind);
        }

        // Delete removes it; the list is empty again and a second delete is a 404.
        var delete = await SendAsync(owner.Client, HttpMethod.Delete, $"/api/items/{itemId}", owner.Token);
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var after = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        using (var doc = await ReadJsonAsync(after))
        {
            Assert.Empty(doc.RootElement.EnumerateArray().ToList());
        }

        var deleteAgain = await SendAsync(owner.Client, HttpMethod.Delete, $"/api/items/{itemId}", owner.Token);
        Assert.Equal(HttpStatusCode.NotFound, deleteAgain.StatusCode);
    }

    // AC 25: completing hides the item from the list view, and the completion is irreversible —
    // re-complete, update, and delete are all 404s; the row keeps its original CompletedAt.
    [Fact]
    public async Task CompleteItem_HidesIt_AndCannotBeUndoneOrRelisted()
    {
        var owner = await CreateUserSessionAsync("items.complete@example.com");
        var listId = await CreateListAsync(owner, "Complete Team");
        var itemId = await CreateItemAsync(owner, listId, "Finish BE-07");

        var complete = await SendAsync(
            owner.Client, new HttpMethod("PATCH"), $"/api/items/{itemId}/complete", owner.Token);
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        // Hidden from the list view.
        var list = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using (var doc = await ReadJsonAsync(list))
        {
            Assert.Empty(doc.RootElement.EnumerateArray().ToList());
        }

        // The row is completed with a stamp…
        DateTime completedAt;
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
            Assert.True(row.IsCompleted);
            Assert.NotNull(row.CompletedAt);
            completedAt = row.CompletedAt!.Value;
        }

        // …and no path can touch it again: re-complete, update, delete are all 404.
        var recomplete = await SendAsync(
            owner.Client, new HttpMethod("PATCH"), $"/api/items/{itemId}/complete", owner.Token);
        Assert.Equal(HttpStatusCode.NotFound, recomplete.StatusCode);

        var update = await SendAsync(
            owner.Client, HttpMethod.Put, $"/api/items/{itemId}", owner.Token,
            Json(new { title = "Zombie edit" }));
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var delete = await SendAsync(owner.Client, HttpMethod.Delete, $"/api/items/{itemId}", owner.Token);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // Still completed, CompletedAt unchanged — completion happened exactly once.
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await ctx.TodoItems.SingleAsync(i => i.Id == itemId);
            Assert.True(row.IsCompleted);
            Assert.Equal("Finish BE-07", row.Title);
            Assert.Equal(completedAt, row.CompletedAt);
        }
    }

    // AC 26, 27: the API returns pre-sorted data from real SQL — ascending by DueDate, null due
    // dates last, and CreateDt ascending as the tiebreak for equal (and null) due dates.
    [Fact]
    public async Task ListItems_IsSortedByDueDateAscending_NullsLast_CreateDtTiebreak()
    {
        var owner = await CreateUserSessionAsync("items.sort@example.com");
        var listId = await CreateListAsync(owner, "Sort Team");

        // Created in an order that differs from the expected output; sequential inserts give
        // strictly increasing CreateDt stamps (datetime2 precision), which the tiebreak pairs rely on.
        await CreateItemAsync(owner, listId, "no-date-first");                     // null due date, created 1st
        await CreateItemAsync(owner, listId, "late", "2026-09-01");
        await CreateItemAsync(owner, listId, "early-first", "2026-08-01");         // created before its twin
        await CreateItemAsync(owner, listId, "no-date-second");                    // null due date, created 4th
        await CreateItemAsync(owner, listId, "early-second", "2026-08-01");
        await CreateItemAsync(owner, listId, "mid", "2026-08-15");

        var response = await owner.Client.GetAsync($"/api/lists/{listId}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = await ReadJsonAsync(response);
        var titles = doc.RootElement.EnumerateArray()
            .Select(i => i.GetProperty("title").GetString())
            .ToArray();

        Assert.Equal(
            new[] { "early-first", "early-second", "mid", "late", "no-date-first", "no-date-second" },
            titles);
    }

    // AC 21/22: missing title and over-long description are 422 business-rule failures with the
    // contractual validation shape; AC 24: a malformed date never reaches the business object —
    // DateOnly binding rejects it as a 400.
    [Fact]
    public async Task CreateItem_ValidationFailures_Return422ForRules_And400ForMalformedDate()
    {
        var owner = await CreateUserSessionAsync("items.validation@example.com");
        var listId = await CreateListAsync(owner, "Validation Team");

        // AC 21: missing title → 422 with the { id, message, errors[], warnings[] } shape.
        var missingTitle = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = "" }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missingTitle.StatusCode);
        using (var doc = await ReadJsonAsync(missingTitle))
        {
            Assert.NotEmpty(doc.RootElement.GetProperty("errors").EnumerateArray().ToList());
        }

        // AC 22: description longer than 200 → 422.
        var longDescription = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = "Valid", description = new string('d', 201) }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longDescription.StatusCode);

        // Title longer than 200 → 422.
        var longTitle = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            Json(new { title = new string('t', 201) }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, longTitle.StatusCode);

        // AC 24: impossible date → 400 from model binding.
        var invalidDate = await SendAsync(
            owner.Client, HttpMethod.Post, $"/api/lists/{listId}/items", owner.Token,
            new StringContent("{\"title\":\"Valid\",\"dueDate\":\"2026-02-30\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, invalidDate.StatusCode);

        // Nothing persisted from any of the rejected requests.
        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Empty(await ctx.TodoItems.ToListAsync());
    }

    // AC 11 + BE-07's list-ownership rule: creating an item on (or listing) a list the user
    // doesn't own is a 404 — indistinguishable from a nonexistent list — and nothing persists;
    // another user's item ids are 404 for update/delete/complete and stay untouched.
    [Fact]
    public async Task Items_CrossUserAccess_IsAlways404AndNeverLeaksOrMutates()
    {
        var userA = await CreateUserSessionAsync("items.isolation.a@example.com");
        var userB = await CreateUserSessionAsync("items.isolation.b@example.com");
        var listB = await CreateListAsync(userB, "B's Team");
        var itemB = await CreateItemAsync(userB, listB, "B's item");

        // A cannot create on B's list — 404, nothing persisted beyond B's item.
        var createOnForeign = await SendAsync(
            userA.Client, HttpMethod.Post, $"/api/lists/{listB}/items", userA.Token,
            Json(new { title = "Intruder" }));
        Assert.Equal(HttpStatusCode.NotFound, createOnForeign.StatusCode);

        // A cannot create on a nonexistent list either — same 404.
        var createOnMissing = await SendAsync(
            userA.Client, HttpMethod.Post, $"/api/lists/{Guid.NewGuid()}/items", userA.Token,
            Json(new { title = "Orphan" }));
        Assert.Equal(HttpStatusCode.NotFound, createOnMissing.StatusCode);

        // A cannot list B's items.
        var listForeign = await userA.Client.GetAsync($"/api/lists/{listB}/items");
        Assert.Equal(HttpStatusCode.NotFound, listForeign.StatusCode);

        // A cannot update, delete, or complete B's item.
        var update = await SendAsync(
            userA.Client, HttpMethod.Put, $"/api/items/{itemB}", userA.Token,
            Json(new { title = "Hijacked" }));
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var delete = await SendAsync(userA.Client, HttpMethod.Delete, $"/api/items/{itemB}", userA.Token);
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        var complete = await SendAsync(
            userA.Client, new HttpMethod("PATCH"), $"/api/items/{itemB}/complete", userA.Token);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);

        // B's item is untouched and B's view is intact.
        using (var scope = _factory.Services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var rows = await ctx.TodoItems.ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(itemB, row.Id);
            Assert.Equal("B's item", row.Title);
            Assert.False(row.IsCompleted);
        }

        var listOwn = await userB.Client.GetAsync($"/api/lists/{listB}/items");
        Assert.Equal(HttpStatusCode.OK, listOwn.StatusCode);
        using (var doc = await ReadJsonAsync(listOwn))
        {
            var only = Assert.Single(doc.RootElement.EnumerateArray().ToList());
            Assert.Equal(itemB, only.GetProperty("id").GetGuid());
        }
    }

    // The ListId FK's ON DELETE CASCADE at the database level: deleting a list's scope entity is
    // out of scope here, but deleting the list row itself must remove its items (spec §3).
    [Fact]
    public async Task DeletingAListRow_CascadesItsItems()
    {
        var owner = await CreateUserSessionAsync("items.cascade@example.com");
        var listId = await CreateListAsync(owner, "Cascade Team");
        var itemId = await CreateItemAsync(owner, listId, "Doomed with the list");

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var list = await ctx.TodoLists.SingleAsync(l => l.Id == listId);
        ctx.TodoLists.Remove(list);
        await ctx.SaveChangesAsync();

        Assert.Empty(await ctx.TodoItems.Where(i => i.Id == itemId).ToListAsync());
    }
}

/// <summary>WebApplicationFactory that points the app at a per-instance throwaway SQL database.</summary>
internal sealed class ItemsApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "ItemsIntTest_" + Guid.NewGuid().ToString("N");

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
