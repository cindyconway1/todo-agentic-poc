using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ToDo.UnitTests;

/// <summary>
/// DB-free smoke tests for the BE-08 read-model routes' wiring: routing and the
/// AuthenticatedUser policy on GET /api/dashboard and GET /api/items/all — via
/// WebApplicationFactory, no SQL Server. A 401 (not a 404 miss, not a 302 redirect) proves each
/// route is mapped, its controller and data-portal dependencies resolve, and auth challenges
/// cleanly. Both routes are GETs with no body, so there are no antiforgery/400 cases;
/// authorized 200 responses need SQL and are covered in the integration tests.
/// </summary>
public class ReadModelsPipelineSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReadModelsPipelineSmokeTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static readonly WebApplicationFactoryClientOptions ClientOptions = new()
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
    };

    public static TheoryData<string> ReadModelRoutes =>
    [
        "/api/dashboard",
        "/api/items/all",
    ];

    [Theory]
    [MemberData(nameof(ReadModelRoutes))]
    public async Task ReadModelRoute_WhenUnauthenticated_Returns401_NotRedirectOr404(string url)
    {
        var response = await _factory.CreateClient(ClientOptions).GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
