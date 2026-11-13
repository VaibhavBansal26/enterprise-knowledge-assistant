using System.Diagnostics;
using System.Globalization;
using Api.Auth;
using Api.Endpoints;
using Api.Hubs;
using Api.Web;
using Application.Abstractions;
using Application.Ingestion;
using FluentValidation;
using Infrastructure;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{CorrelationId}] {Message:lj}{NewLine}{Exception}",
        formatProvider: CultureInfo.InvariantCulture));

// JSON API: cap request bodies. Upload endpoints (Phase 4) raise this per endpoint.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 1_048_576);

builder.Services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier;
    if (ctx.HttpContext.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var correlationId))
    {
        ctx.ProblemDetails.Extensions["correlationId"] = correlationId;
    }
});

// Handlers run in registration order: specific first, catch-all last.
builder.Services.AddExceptionHandler<ConcurrencyExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddResponseCompression(); // Brotli + gzip; HTTPS compression stays off by default (BREACH)
builder.Services.AddWebCors(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddAppAuth(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddValidatorsFromAssemblyContaining<Application.AssemblyMarker>();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSignalR().AddRedisBackplane();
// Replaces the infrastructure's no-op: ingestion progress now reaches browsers through the hub.
builder.Services.AddSingleton<IIngestionNotifier, SignalRIngestionNotifier>();

var app = builder.Build();

// Order matters. Correlation first so every later log line and error carries the ID; the exception
// handler wraps everything after it; the rate limiter runs before the (costlier) idempotency check.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
// Turns body-less 401/403/404 responses (from auth, routing) into ProblemDetails like every other error.
app.UseStatusCodePages();
app.UseSerilogRequestLogging(o =>
{
    o.MessageTemplate = "HTTP {RequestMethod} {RequestPath}{QueryString} responded {StatusCode} in {Elapsed:0.0000} ms";
    o.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 500 ? LogEventLevel.Error
        : ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase) ? LogEventLevel.Verbose
        : LogEventLevel.Information;
    o.EnrichDiagnosticContext = (diagnostics, ctx) =>
    {
        diagnostics.Set("QueryString", LogRedactor.RedactQuery(ctx.Request.QueryString));
        diagnostics.Set("ClientIp", ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown");
    };
});
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseResponseCompression();
app.UseCors(WebCorsSettings.PolicyName);
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
// Rate limiting sits before authorization so unauthenticated floods are throttled too; idempotency sits
// after it so a refused (401/403) request is never stored for replay.
app.UseMiddleware<RateLimitMiddleware>();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

if (app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MigrateOnStartup)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

// Liveness: process is up. Readiness: dependencies (Postgres, Redis) are reachable.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains(DependencyInjection.ReadyTag)
}).AllowAnonymous();

app.MapGet("/api/v1/ping", () => Results.Ok(new { message = "pong" })).AllowAnonymous();
app.MapAuditAndMeEndpoints();
app.MapMemberEndpoints();
app.MapHub<IngestionHub>(IngestionHub.Path);
app.MapWorkspaceEndpoints();
app.MapDocumentEndpoints();

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
