using System.Reflection;
using Application.Safety;
using Application.Telemetry;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Sentry;

namespace Api.Web;

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>OTLP collector endpoint (e.g. http://localhost:4317). Empty disables export; instruments still run.</summary>
    public string? OtlpEndpoint { get; init; }

    public string ServiceName { get; init; } = "knowledge-assistant";
}

public static class Observability
{
    /// <summary>Histogram boundaries (milliseconds) suited to chat latency, so p50/p95/p99 are meaningful.</summary>
    private static readonly double[] LatencyBucketsMs = [25, 50, 100, 250, 500, 1000, 2000, 4000, 8000, 15000, 30000, 60000];
    private static readonly double[] ScoreBuckets = [0.25, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0];
    private static readonly string[] LatencyInstruments = ["kb.chat.duration", "kb.chat.time_to_first_token", "kb.retrieval.duration", "kb.ingestion.duration"];

    public static IServiceCollection AddObservability(this IServiceCollection services, IConfiguration config, string environment)
    {
        var settings = config.GetSection(TelemetryOptions.SectionName).Get<TelemetryOptions>() ?? new();
        var version = typeof(Observability).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(settings.ServiceName, serviceVersion: version)
                .AddAttributes([new("deployment.environment", environment)]))
            .WithTracing(tracing => tracing
                .AddSource(AppTelemetry.Name)
                .AddSource("Npgsql") // Npgsql emits its own spans: one per SQL command
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation())
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AppTelemetry.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                foreach (var name in LatencyInstruments)
                {
                    metrics.AddView(name, new ExplicitBucketHistogramConfiguration { Boundaries = LatencyBucketsMs });
                }

                metrics.AddView("kb.chat.grounding_score", new ExplicitBucketHistogramConfiguration { Boundaries = ScoreBuckets });
            });

        if (!string.IsNullOrWhiteSpace(settings.OtlpEndpoint))
        {
            otel.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(settings.OtlpEndpoint));
        }

        return services;
    }

    /// <summary>Error tracking. Without <c>Sentry:Dsn</c> the SDK stays disabled and sends nothing.</summary>
    public static void UseErrorTracking(this ConfigureWebHostBuilder webHost, string environment)
    {
        webHost.UseSentry((context, options) =>
        {
            options.Dsn = context.Configuration["Sentry:Dsn"] ?? string.Empty;
            options.Environment = environment;
            options.SendDefaultPii = false;          // no IPs, cookies or user details
            options.MaxRequestBodySize = Sentry.Extensibility.RequestSize.None; // never capture request bodies (questions, uploads)
            options.TracesSampleRate = 0;            // performance traces go through OpenTelemetry
            options.MinimumEventLevel = LogLevel.Error;
            options.SetBeforeSend(SentryScrubber.Scrub);
        });
    }
}

/// <summary>Removes anything that could identify a person or leak content before an event leaves the process.</summary>
public static class SentryScrubber
{
    public static SentryEvent? Scrub(SentryEvent sentryEvent)
    {
        ArgumentNullException.ThrowIfNull(sentryEvent);

        sentryEvent.User = new SentryUser();
        sentryEvent.Request.Cookies = null;
        sentryEvent.Request.Data = null;
        sentryEvent.Request.QueryString = LogRedactor.RedactQuery(new QueryString(string.IsNullOrEmpty(sentryEvent.Request.QueryString) ? string.Empty : "?" + sentryEvent.Request.QueryString.TrimStart('?')));
        foreach (var header in sentryEvent.Request.Headers.Keys.ToList())
        {
            if (header is "Authorization" or "Cookie" or "X-Api-Key" || header.Contains("token", StringComparison.OrdinalIgnoreCase))
            {
                sentryEvent.Request.Headers[header] = LogRedactor.Mask;
            }
        }

        if (sentryEvent.Message is { } message)
        {
            message.Message = message.Message is null ? null : PiiRedactor.Redact(message.Message).Text;
            message.Formatted = message.Formatted is null ? null : PiiRedactor.Redact(message.Formatted).Text;
        }

        return sentryEvent;
    }
}
