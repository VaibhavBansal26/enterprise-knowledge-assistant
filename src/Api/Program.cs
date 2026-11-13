using System.Globalization;
using Api.Endpoints;
using Api.Web;
using FluentValidation;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture));

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssemblyContaining<Application.AssemblyMarker>();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

// Liveness: process is up. Readiness: dependencies (Postgres, Redis) are reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(DependencyInjection.ReadyTag)
});

app.MapGet("/api/v1/ping", () => Results.Ok(new { message = "pong" }));
app.MapWorkspaceEndpoints();
app.MapDocumentEndpoints();

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
