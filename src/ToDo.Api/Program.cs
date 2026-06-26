using Csla.Configuration;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();   // needed for ToDoController later
builder.Services.AddCsla();          // registers IDataPortal<T>, ApplicationContext, etc.
builder.Services.AddOpenApi();       // Swagger/OpenAPI (kept from template)

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.MapControllers();                                   // routes for your API controllers
app.MapGet("/health", () => Results.Ok("ok"));          // simple liveness check

app.Run();