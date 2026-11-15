using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Infrastructure.Llm;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";
    public const string ExtractiveProvider = "extractive";
    public const string AnthropicProvider = "anthropic";

    /// <summary>"extractive" (offline fallback, no API key) or "anthropic".</summary>
    [Required]
    public string Provider { get; init; } = ExtractiveProvider;

    public AnthropicOptions Anthropic { get; init; } = new();
}

public sealed class AnthropicOptions
{
    /// <summary>Set through user secrets or an environment variable (Llm__Anthropic__ApiKey), never in a file.</summary>
    public string? ApiKey { get; init; }

    /// <summary>Override only for tests or a gateway; defaults to the public API.</summary>
    public string? BaseUrl { get; init; }

    public string Model { get; init; } = "claude-opus-5-5";

    /// <summary>"low", "medium", "high", "max". Thinking cannot be turned off on this model, so effort is the lever; chat Q&amp;A over supplied sources rarely needs more than low.</summary>
    public string Effort { get; init; } = "low";

    /// <summary>The SDK retries 408/409/429/5xx and connection errors itself (honouring Retry-After).</summary>
    /// <summary>
    /// When a request is declined by the model's safety classifiers, re-serve it on this model within the same call
    /// (server-side refusal fallback). Leave empty to disable.
    /// </summary>
    public string? RefusalFallbackModel { get; init; } = "claude-opus-4-8";

    [Range(0, 10)]
    public int MaxRetries { get; init; } = 3;

    [Range(1, 600)]
    public int RequestTimeoutSeconds { get; init; } = 120;

    /// <summary>Longest to wait for the first streamed event; slower counts as a failure toward the circuit breaker.</summary>
    [Range(1, 300)]
    public int FirstEventTimeoutSeconds { get; init; } = 30;

    [Range(0.05, 1.0)]
    public double BreakerFailureRatio { get; init; } = 0.5;

    [Range(1, 1000)]
    public int BreakerMinimumThroughput { get; init; } = 5;

    [Range(1, 600)]
    public int BreakerSamplingSeconds { get; init; } = 30;

    [Range(1, 3600)]
    public int BreakerBreakSeconds { get; init; } = 30;
}

public sealed class LlmOptionsValidator : IValidateOptions<LlmOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Provider is not (LlmOptions.ExtractiveProvider or LlmOptions.AnthropicProvider))
        {
            return ValidateOptionsResult.Fail($"Llm:Provider must be '{LlmOptions.ExtractiveProvider}' or '{LlmOptions.AnthropicProvider}'.");
        }

        if (options.Provider == LlmOptions.AnthropicProvider && string.IsNullOrWhiteSpace(options.Anthropic.ApiKey))
        {
            return ValidateOptionsResult.Fail("Llm:Anthropic:ApiKey is required when Llm:Provider is 'anthropic'.");
        }

        return ValidateOptionsResult.Success;
    }
}

public sealed class EmbeddingsOptions
{
    public const string SectionName = "Embeddings";
    public const string HashingProvider = "hashing";
    public const string OpenAiProvider = "openai";

    /// <summary>"hashing" (offline stand-in) or "openai" (any OpenAI-compatible /embeddings endpoint).</summary>
    [Required]
    public string Provider { get; init; } = HashingProvider;

    public OpenAiEmbeddingOptions OpenAi { get; init; } = new();
}

public sealed class OpenAiEmbeddingOptions
{
    public string BaseUrl { get; init; } = "https://api.openai.com/v1/";
    public string? ApiKey { get; init; }

    /// <summary>The model must support the <c>dimensions</c> parameter (e.g. text-embedding-3-*) so vectors match the 384-d column.</summary>
    public string Model { get; init; } = "text-embedding-3-small";

    [Range(1, 2048)]
    public int BatchSize { get; init; } = 64;

    [Range(1, 10)]
    public int MaxRetryAttempts { get; init; } = 3;

    [Range(0, 60_000)]
    public int RetryDelayMs { get; init; } = 500;

    [Range(1, 300)]
    public int AttemptTimeoutSeconds { get; init; } = 20;

    [Range(1, 900)]
    public int TotalTimeoutSeconds { get; init; } = 60;

    [Range(1, 3600)]
    public int BreakSeconds { get; init; } = 30;

    /// <summary>Calls in the sampling window before the breaker may open (the library default of 100 suits busy services, not this one).</summary>
    [Range(1, 1000)]
    public int BreakerMinimumThroughput { get; init; } = 10;

    [Range(0.05, 1.0)]
    public double BreakerFailureRatio { get; init; } = 0.5;
}

public sealed class EmbeddingsOptionsValidator : IValidateOptions<EmbeddingsOptions>
{
    public ValidateOptionsResult Validate(string? name, EmbeddingsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Provider is not (EmbeddingsOptions.HashingProvider or EmbeddingsOptions.OpenAiProvider))
        {
            return ValidateOptionsResult.Fail($"Embeddings:Provider must be '{EmbeddingsOptions.HashingProvider}' or '{EmbeddingsOptions.OpenAiProvider}'.");
        }

        if (options.Provider == EmbeddingsOptions.OpenAiProvider && string.IsNullOrWhiteSpace(options.OpenAi.ApiKey))
        {
            return ValidateOptionsResult.Fail("Embeddings:OpenAi:ApiKey is required when Embeddings:Provider is 'openai'.");
        }

        return ValidateOptionsResult.Success;
    }
}
