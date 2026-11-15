using System.ComponentModel.DataAnnotations;
using Application.Abstractions;
using Application.Safety;
using Application.Usage;
using Domain;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Infrastructure.Usage;

public sealed class UsageOptions
{
    public const string SectionName = "Usage";

    /// <summary>Default monthly token cap per tenant when none is set. 0 means unlimited.</summary>
    [Range(0, long.MaxValue)]
    public long DefaultMonthlyTokenLimit { get; init; } = 2_000_000;

    /// <summary>Default monthly estimated-cost cap per tenant, in USD. 0 means unlimited.</summary>
    [Range(0, 1_000_000)]
    public decimal DefaultMonthlyCostLimitUsd { get; init; } = 25m;

    /// <summary>Answers scoring below this grounding score count as "low grounding" on the dashboard.</summary>
    [Range(0.0, 1.0)]
    public double LowGroundingThreshold { get; init; } = 0.5;
}

public sealed class ModelPriceOptions
{
    public decimal InputPerMTok { get; init; }
    public decimal OutputPerMTok { get; init; }
}

public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>Overrides of the built-in per-model prices, keyed by model name.</summary>
    public Dictionary<string, ModelPriceOptions> Models { get; init; } = [];

    public IReadOnlyDictionary<string, ModelPrice> AsPrices() =>
        Models.ToDictionary(m => m.Key, m => new ModelPrice(m.Value.InputPerMTok, m.Value.OutputPerMTok), StringComparer.Ordinal);
}

/// <summary>Cost estimation, monthly quotas, and the aggregated numbers behind the operations dashboard.</summary>
public sealed class UsageService(
    AppDbContext db,
    NpgsqlDataSource dataSource,
    ITenantContext tenant,
    IOptions<UsageOptions> usageOptions,
    IOptions<PricingOptions> pricingOptions)
{
    private Guid TenantId => tenant.TenantId ?? throw new InvalidOperationException("No tenant for usage request.");

    public decimal CostOf(string model, long inputTokens, long outputTokens) =>
        PricingCatalog.Cost(inputTokens, outputTokens, PricingCatalog.PriceOf(model, pricingOptions.Value.AsPrices()));

    private static DateTimeOffset MonthStart(DateTimeOffset now) => new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<QuotaStatusResponse> GetQuotaStatusAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = MonthStart(now);

        var used = await db.UsageLedger
            .Where(e => e.CreatedAt >= start)
            .GroupBy(_ => 1)
            .Select(g => new { Tokens = g.Sum(e => e.InputTokens + e.OutputTokens), Cost = g.Sum(e => e.CostUsd) })
            .FirstOrDefaultAsync(ct);
        var tokens = used?.Tokens ?? 0;
        var cost = used?.Cost ?? 0m;

        var custom = await db.TenantQuotas.AsNoTracking().FirstOrDefaultAsync(ct);
        var tokenLimit = custom is not null ? custom.MonthlyTokenLimit : NullIfZero(usageOptions.Value.DefaultMonthlyTokenLimit);
        var costLimit = custom is not null ? custom.MonthlyCostLimitUsd : NullIfZero(usageOptions.Value.DefaultMonthlyCostLimitUsd);

        var exceeded = (tokenLimit is { } t && tokens >= t) || (costLimit is { } c && cost >= c);
        return new QuotaStatusResponse(tokenLimit, costLimit, tokens, cost, exceeded, start.AddMonths(1), IsDefault: custom is null);
    }

    private static T? NullIfZero<T>(T value)
        where T : struct, IComparable<T> =>
        value.CompareTo(default) == 0 ? null : value;

    public async Task SetQuotaAsync(long? tokenLimit, decimal? costLimit, CancellationToken ct)
    {
        var existing = await db.TenantQuotas.FirstOrDefaultAsync(ct);
        if (existing is null)
        {
            db.TenantQuotas.Add(new TenantQuota(TenantId, tokenLimit, costLimit));
        }
        else
        {
            existing.Set(tokenLimit, costLimit);
        }

        await db.SaveChangesAsync(ct);
    }

    private const string SummarySql = """
        SELECT
          count(*) FILTER (WHERE kind = 'chat'),
          count(*) FILTER (WHERE kind = 'chat' AND outcome = 'answered'),
          count(*) FILTER (WHERE kind = 'chat' AND outcome = 'cached'),
          count(*) FILTER (WHERE kind = 'chat' AND outcome = 'no_sources'),
          count(*) FILTER (WHERE kind = 'chat' AND outcome = 'refused'),
          count(*) FILTER (WHERE kind = 'chat' AND outcome = 'error'),
          count(*) FILTER (WHERE kind = 'chat' AND outcome = 'cancelled'),
          count(*) FILTER (WHERE kind = 'chat' AND outcome = 'quota_exceeded'),
          COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY latency_ms) FILTER (WHERE kind = 'chat' AND latency_ms IS NOT NULL AND outcome IN ('answered','cached','no_sources','refused')), 0),
          COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms) FILTER (WHERE kind = 'chat' AND latency_ms IS NOT NULL AND outcome IN ('answered','cached','no_sources','refused')), 0),
          COALESCE(percentile_cont(0.99) WITHIN GROUP (ORDER BY latency_ms) FILTER (WHERE kind = 'chat' AND latency_ms IS NOT NULL AND outcome IN ('answered','cached','no_sources','refused')), 0),
          COALESCE(percentile_cont(0.50) WITHIN GROUP (ORDER BY time_to_first_token_ms) FILTER (WHERE kind = 'chat' AND time_to_first_token_ms IS NOT NULL), 0),
          COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY time_to_first_token_ms) FILTER (WHERE kind = 'chat' AND time_to_first_token_ms IS NOT NULL), 0),
          COALESCE(percentile_cont(0.99) WITHIN GROUP (ORDER BY time_to_first_token_ms) FILTER (WHERE kind = 'chat' AND time_to_first_token_ms IS NOT NULL), 0),
          COALESCE(sum(input_tokens), 0),
          COALESCE(sum(output_tokens), 0),
          COALESCE(sum(cost_usd), 0),
          avg(grounding_score) FILTER (WHERE grounding_score IS NOT NULL),
          count(*) FILTER (WHERE grounding_score IS NOT NULL AND grounding_score < @lowGrounding),
          COALESCE(sum(neutralized), 0),
          COALESCE(sum(pii_redactions), 0)
        FROM usage_ledger
        WHERE tenant_id = @tenant AND created_at >= @since
        """;

    private const string DailySql = """
        SELECT date_trunc('day', created_at) AS day,
               count(*) FILTER (WHERE kind = 'chat'),
               COALESCE(sum(cost_usd), 0),
               COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY latency_ms) FILTER (WHERE kind = 'chat' AND latency_ms IS NOT NULL AND outcome IN ('answered','cached','no_sources','refused')), 0)
          FROM usage_ledger
         WHERE tenant_id = @tenant AND created_at >= @since
         GROUP BY 1 ORDER BY 1
        """;

    private const string ModelSql = """
        SELECT model, count(*), COALESCE(sum(input_tokens), 0), COALESCE(sum(output_tokens), 0), COALESCE(sum(cost_usd), 0)
          FROM usage_ledger
         WHERE tenant_id = @tenant AND created_at >= @since AND model <> 'none'
         GROUP BY model ORDER BY sum(cost_usd) DESC, count(*) DESC
        """;

    private const string UserSql = """
        SELECT COALESCE(user_id, 'system'), count(*) FILTER (WHERE kind = 'chat'), COALESCE(sum(cost_usd), 0)
          FROM usage_ledger
         WHERE tenant_id = @tenant AND created_at >= @since
         GROUP BY 1 ORDER BY sum(cost_usd) DESC, count(*) DESC LIMIT 10
        """;

    public async Task<OpsSummaryResponse> GetSummaryAsync(int days, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var tenantId = TenantId;

        NpgsqlCommand Command(string sql)
        {
            var cmd = dataSource.CreateCommand(sql);
            cmd.Parameters.AddWithValue("tenant", tenantId);
            cmd.Parameters.AddWithValue("since", since);
            return cmd;
        }

        OutcomeCounts outcomes;
        LatencyStats latency;
        LatencyStats firstToken;
        long input, output;
        decimal totalCost;
        double? grounding;
        int lowGrounding, neutralized, pii;

        await using (var cmd = Command(SummarySql))
        {
            cmd.Parameters.AddWithValue("lowGrounding", usageOptions.Value.LowGroundingThreshold);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            outcomes = new OutcomeCounts(
                (int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3),
                (int)r.GetInt64(4), (int)r.GetInt64(5), (int)r.GetInt64(6), (int)r.GetInt64(7));
            latency = new LatencyStats(r.GetDouble(8), r.GetDouble(9), r.GetDouble(10));
            firstToken = new LatencyStats(r.GetDouble(11), r.GetDouble(12), r.GetDouble(13));
            input = r.GetInt64(14);
            output = r.GetInt64(15);
            totalCost = r.GetDecimal(16);
            grounding = r.IsDBNull(17) ? null : r.GetDouble(17);
            lowGrounding = (int)r.GetInt64(18);
            neutralized = (int)r.GetInt64(19);
            pii = (int)r.GetInt64(20);
        }

        var daily = new List<DailyPoint>();
        await using (var cmd = Command(DailySql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                daily.Add(new DailyPoint(DateOnly.FromDateTime(r.GetDateTime(0)), (int)r.GetInt64(1), r.GetDecimal(2), r.GetDouble(3)));
            }
        }

        var models = new List<ModelUsage>();
        await using (var cmd = Command(ModelSql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                models.Add(new ModelUsage(r.GetString(0), (int)r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetDecimal(4)));
            }
        }

        var users = new List<UserUsage>();
        await using (var cmd = Command(UserSql))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                users.Add(new UserUsage(r.GetString(0), (int)r.GetInt64(1), r.GetDecimal(2)));
            }
        }

        var questions = Math.Max(outcomes.Questions, 1);
        return new OpsSummaryResponse(
            days,
            outcomes,
            latency,
            firstToken,
            input,
            output,
            totalCost,
            outcomes.Questions == 0 ? 0m : totalCost / outcomes.Questions,
            (double)outcomes.Cached / questions,
            (double)outcomes.NoSources / questions,
            (double)outcomes.Error / questions,
            grounding,
            lowGrounding,
            neutralized,
            pii,
            daily,
            models,
            users,
            await GetQuotaStatusAsync(ct));
    }
}
