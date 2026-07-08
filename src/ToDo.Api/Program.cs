using Csla.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using ToDo.Api.Auth;
using ToDo.Api.Services;
using ToDo.Business.Services;
using ToDo.DataAccess;

var builder = WebApplication.CreateBuilder(args);

// AddControllersWithViews (not AddControllers) so the MVC antiforgery filter service
// (ValidateAntiforgeryTokenAuthorizationFilter) backing [ValidateAntiForgeryToken] is registered.
builder.Services.AddControllersWithViews();
builder.Services.AddHttpContextAccessor();
builder.Services.AddCsla(o => o.AddAspNetCore());
builder.Services.AddOpenApi();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddScoped<ICurrentUserAccessor, CslaCurrentUserAccessor>();
builder.Services.AddSingleton<IPasswordHasher, Argon2IdPasswordHasher>();

// Vite dev-proxy wiring (spec §5): the frontend dev server proxies /api to this
// backend so browser requests are same-origin, letting Secure + SameSite=Strict
// cookies work without CORS or dev-only cookie relaxation.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.AuthenticatedUser, policy => policy.RequireAuthenticatedUser());
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-XSRF-TOKEN");

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health", () => Results.Ok("ok"));

app.Run();

public partial class Program { }
