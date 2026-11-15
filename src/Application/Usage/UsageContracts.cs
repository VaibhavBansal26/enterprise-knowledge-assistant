using FluentValidation;

namespace Application.Usage;

public sealed record QuotaStatusResponse(
    long? MonthlyTokenLimit,
    decimal? MonthlyCostLimitUsd,
    long TokensUsed,
    decimal CostUsedUsd,
    bool Exceeded,
    DateTimeOffset ResetsAt,
    bool IsDefault);

/// <summary>Null means unlimited. Zero is not a useful limit and is rejected.</summary>
public sealed record SetQuotaRequest(long? MonthlyTokenLimit, decimal? MonthlyCostLimitUsd);

public sealed class SetQuotaRequestValidator : AbstractValidator<SetQuotaRequest>
{
    public SetQuotaRequestValidator()
    {
        RuleFor(x => x.MonthlyTokenLimit).GreaterThan(0).When(x => x.MonthlyTokenLimit.HasValue);
        RuleFor(x => x.MonthlyCostLimitUsd).GreaterThan(0).LessThanOrEqualTo(1_000_000m).When(x => x.MonthlyCostLimitUsd.HasValue);
    }
}

public sealed record OutcomeCounts(
    int Questions, int Answered, int Cached, int NoSources, int Refused, int Error, int Cancelled, int QuotaExceeded);

public sealed record LatencyStats(double P50Ms, double P95Ms, double P99Ms);

public sealed record DailyPoint(DateOnly Day, int Questions, decimal CostUsd, double P95LatencyMs);

public sealed record ModelUsage(string Model, int Requests, long InputTokens, long OutputTokens, decimal CostUsd);

public sealed record UserUsage(string UserId, int Questions, decimal CostUsd);

public sealed record OpsSummaryResponse(
    int Days,
    OutcomeCounts Outcomes,
    LatencyStats Latency,
    LatencyStats FirstToken,
    long InputTokens,
    long OutputTokens,
    decimal TotalCostUsd,
    decimal CostPerQuestionUsd,
    double CacheHitRate,
    double NoSourceRate,
    double ErrorRate,
    double? AverageGrounding,
    int LowGroundingAnswers,
    int InjectionNeutralized,
    int PiiRedactions,
    IReadOnlyList<DailyPoint> Daily,
    IReadOnlyList<ModelUsage> Models,
    IReadOnlyList<UserUsage> TopUsers,
    QuotaStatusResponse Quota);

public sealed record EvalMetric(string Name, double Value, double? Baseline, double? Delta, string Status);

public sealed record EvalReportResponse(
    DateTimeOffset RanAt, string Provider, int Cases, bool Passed, IReadOnlyList<EvalMetric> Metrics, IReadOnlyList<string> Failures);
