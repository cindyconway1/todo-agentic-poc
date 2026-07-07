using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ToDo.Api.Dtos;

namespace ToDo.IntegrationTests;

public class AuthControllerTests : IClassFixture<AuthApiFactory>
{
    private readonly AuthApiFactory _factory;

    public AuthControllerTests(AuthApiFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<(HttpClient Client, string XsrfToken)> CreateClientWithAntiforgeryAsync()
    {
        var client = CreateClient();
        var response = await client.GetAsync("/api/auth/antiforgery");
        response.EnsureSuccessStatusCode();

        var setCookies = response.Headers.GetValues("Set-Cookie").ToList();
        foreach (var setCookie in setCookies)
        {
            client.DefaultRequestHeaders.Add("Cookie", setCookie.Split(';')[0]);
        }

        var xsrfCookie = setCookies.First(c => c.StartsWith("XSRF-TOKEN"));
        var xsrfToken = xsrfCookie.Split(';')[0].Split('=', 2)[1];

        return (client, xsrfToken);
    }

    [Fact]
    public async Task Register_WithValidData_ReturnsCreated()
    {
        var (client, token) = await CreateClientWithAntiforgeryAsync();
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"{Guid.NewGuid():N}@example.com", "validpassword1"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<UserDto>();
        Assert.NotNull(user);
        Assert.NotEqual(Guid.Empty, user!.Id);
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_ReturnsConflict()
    {
        var (client, token) = await CreateClientWithAntiforgeryAsync();
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
        var email = $"{Guid.NewGuid():N}@example.com";

        var first = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "validpassword1"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "validpassword1"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task Login_WithValidCredentials_SetsCookieWithCorrectFlags()
    {
        var (client, token) = await CreateClientWithAntiforgeryAsync();
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
        var email = $"{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "validpassword1"));

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "validpassword1"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var authCookie = response.Headers.GetValues("Set-Cookie").First(c => c.Contains(".AspNetCore.Cookies"));
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", authCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsGenericUnauthorized()
    {
        var (client, token) = await CreateClientWithAntiforgeryAsync();
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);
        var email = $"{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest(email, "validpassword1"));

        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "wrong-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_ReturnsSameGenericUnauthorized()
    {
        var (client, token) = await CreateClientWithAntiforgeryAsync();
        client.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);

        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest($"{Guid.NewGuid():N}@example.com", "any-password"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithoutAuthentication_ReturnsUnauthorized()
    {
        var client = CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithoutAntiforgeryToken_IsRejected()
    {
        var client = CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest($"{Guid.NewGuid():N}@example.com", "validpassword1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
