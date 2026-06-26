using Csla.Configuration;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using ToDo.Api.Services;
using ToDo.DataAccess;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddCsla();
builder.Services.AddOpenApi();

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddScoped<ICurrentUserAccessor, CslaCurrentUserAccessor>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.MapControllers();
app.MapGet("/health", () => Results.Ok("ok"));

app.Run();

public partial class Program { }
