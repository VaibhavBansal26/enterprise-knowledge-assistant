using System.Text.Json;
using Api.Auth;
using Application.Usage;
using Infrastructure.Usage;
using Microsoft.Extensions.Options;

namespace Api.Endpoints;

public sealed class EvalsOptions
{
    public const string SectionName = "Evals";

    /// <summary>
    /// Where the latest eval report (JSON written by the eval harness) is read from, relative to the app's content root
    /// unless absolute. In production CI should publish the report here (a mounted volume or synced file).
    /// </summary>
    public string ResultsPath { get; init; } = "evals/results/latest.json";
}

/// <summary>Operations dashboard data for tenant administrators: usage, latency, cost, quota and evaluation scores.</summary>
public static class OpsEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin").WithTags("Operations").RequireAuthorization(Policies.Admin);

        group.MapGet("/ops/summary", Summary);
        group.MapGet("/quota", GetQuota);
        group.MapPut("/quota", SetQuota).AddEndpointFilter<Web.ValidationFilter<SetQuotaRequest>>();
        group.MapGet("/evals/latest", LatestEvals);

        return app;
    }

    private static async Task<IResult> Summary(int? days, UsageService usage, CancellationToken ct)
    {
        var window = days ?? 7;
        if (window is < 1 or > 90)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["days must be between 1 and 90."] });
        }

        return Results.Ok(await usage.GetSummaryAsync(window, ct));
    }

    private static async Task<IResult> GetQuota(UsageService usage, CancellationToken ct) =>
        Results.Ok(await usage.GetQuotaStatusAsync(ct));

    private static async Task<IResult> SetQuota(SetQuotaRequest request, UsageService usage, CancellationToken ct)
    {
        await usage.SetQuotaAsync(request.MonthlyTokenLimit, request.MonthlyCostLimitUsd, ct);
        return Results.Ok(await usage.GetQuotaStatusAsync(ct));
    }

    private static async Task<IResult> LatestEvals(IOptions<EvalsOptions> options, IHostEnvironment env, CancellationToken ct)
    {
        var path = Path.IsPathRooted(options.Value.ResultsPath)
            ? options.Value.ResultsPath
            : Path.Combine(env.ContentRootPath, options.Value.ResultsPath);

        if (!File.Exists(path))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "No evaluation report yet",
                detail: "Run the eval suite (dotnet test --filter Category=Eval) or publish a report to the configured path.");
        }

        await using var stream = File.OpenRead(path);
        var report = await JsonSerializer.DeserializeAsync<EvalReportResponse>(stream, Json, ct);
        return report is null ? Results.Problem(statusCode: 500, title: "Unreadable evaluation report") : Results.Ok(report);
    }
}
