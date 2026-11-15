using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Application.Telemetry;

/// <summary>
/// The application's own traces and metrics (OpenTelemetry-compatible, built on System.Diagnostics, so the domain
/// code has no dependency on any exporter). Hosts decide where the data goes. Rules: never put question or answer
/// text, document text, or personal data into a span or a metric tag; use ids, counts and lengths.
/// </summary>
public static class AppTelemetry
{
    public const string Name = "KnowledgeAssistant";

    public static readonly ActivitySource Source = new(Name);
    public static readonly Meter Meter = new(Name);

    // ---- chat ----
    public static readonly Counter<long> ChatRequests = Meter.CreateCounter<long>(
        "kb.chat.requests", description: "Chat questions, by outcome (answered, cached, no_sources, refused, error, cancelled, quota_exceeded).");

    public static readonly Histogram<double> ChatDuration = Meter.CreateHistogram<double>(
        "kb.chat.duration", unit: "ms", description: "Time from question to finished answer.");

    public static readonly Histogram<double> TimeToFirstToken = Meter.CreateHistogram<double>(
        "kb.chat.time_to_first_token", unit: "ms", description: "Time from question to the first piece of the answer.");

    public static readonly Histogram<double> RetrievalDuration = Meter.CreateHistogram<double>(
        "kb.retrieval.duration", unit: "ms", description: "Hybrid search time.");

    public static readonly Histogram<int> RetrievedPassages = Meter.CreateHistogram<int>(
        "kb.retrieval.passages", description: "Passages returned by retrieval per question.");

    public static readonly Histogram<double> GroundingScore = Meter.CreateHistogram<double>(
        "kb.chat.grounding_score", description: "Fraction of answer sentences supported by their sources (0-1).");

    // ---- cost ----
    public static readonly Counter<long> LlmTokens = Meter.CreateCounter<long>(
        "kb.llm.tokens", unit: "{token}", description: "Model tokens used, by direction (input/output) and model.");

    public static readonly Counter<double> LlmCostUsd = Meter.CreateCounter<double>(
        "kb.llm.cost", unit: "USD", description: "Estimated model spend (from configured prices).");

    // ---- cache, safety ----
    public static readonly Counter<long> CacheLookups = Meter.CreateCounter<long>(
        "kb.cache.lookups", description: "Semantic cache lookups, by result (hit, miss, denied, error).");

    public static readonly Counter<long> InjectionNeutralized = Meter.CreateCounter<long>(
        "kb.safety.injection_neutralized", description: "Instruction-like sentences removed from retrieved passages.");

    public static readonly Counter<long> PiiRedactions = Meter.CreateCounter<long>(
        "kb.safety.pii_redactions", description: "Personal data items masked in questions, by kind.");

    public static readonly Counter<long> LeakBlocked = Meter.CreateCounter<long>(
        "kb.safety.prompt_leak_blocked", description: "Answers withheld because they repeated the system prompt.");

    public static readonly Counter<long> QuotaRejected = Meter.CreateCounter<long>(
        "kb.quota.rejected", description: "Questions refused because the tenant's monthly quota was used up.");

    // ---- ingestion ----
    public static readonly Histogram<double> IngestionDuration = Meter.CreateHistogram<double>(
        "kb.ingestion.duration", unit: "ms", description: "Time to parse, chunk, embed and store one document.");

    public static readonly Counter<long> IngestionOutcomes = Meter.CreateCounter<long>(
        "kb.ingestion.outcomes", description: "Document ingestions by outcome (ready, failed).");
}
