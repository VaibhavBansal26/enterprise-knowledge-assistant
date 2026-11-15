using System.Runtime.CompilerServices;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Messages;
using Beta = Anthropic.Models.Beta.Messages;
using Application.Chat;
using Microsoft.Extensions.Options;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Infrastructure.Llm;

/// <summary>
/// Streams answers from Claude through the official Anthropic SDK.
///
/// Resilience is layered: the SDK itself retries 408/409/429/5xx and connection errors with backoff, honouring
/// <c>Retry-After</c> (see <c>Llm:Anthropic:MaxRetries</c>). On top of that a Polly pipeline guards the start of
/// each stream with a first-event timeout and a circuit breaker, so a provider outage makes chat fail fast with a
/// clear message instead of stacking up slow, doomed requests. Retrying once tokens have started to flow is not
/// safe (the user has already seen text), so mid-stream failures surface as errors.
/// </summary>
public sealed class AnthropicLlmClient : ILlmClient, IDisposable
{
    private readonly AnthropicClient _client;
    private readonly AnthropicOptions _options;
    private readonly ResiliencePipeline _pipeline;

    public AnthropicLlmClient(IOptions<LlmOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value.Anthropic;

        var timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
        _client = string.IsNullOrWhiteSpace(_options.BaseUrl)
            ? new AnthropicClient { ApiKey = _options.ApiKey, MaxRetries = _options.MaxRetries, Timeout = timeout }
            : new AnthropicClient { ApiKey = _options.ApiKey, MaxRetries = _options.MaxRetries, Timeout = timeout, BaseUrl = _options.BaseUrl };

        // Outermost first: the breaker sees timeouts and provider errors from the stage inside it.
        _pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = _options.BreakerFailureRatio,
                MinimumThroughput = _options.BreakerMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(_options.BreakerSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(_options.BreakerBreakSeconds),
                ShouldHandle = new PredicateBuilder()
                    .Handle<TimeoutRejectedException>()
                    .Handle<HttpRequestException>()
                    .Handle<AnthropicApiException>(IsProviderProblem),
            })
            .AddTimeout(TimeSpan.FromSeconds(_options.FirstEventTimeoutSeconds))
            .Build();
    }

    public string ModelId => _options.Model;

    public void Dispose() => _client.Dispose();

    /// <summary>Rate limits and server errors reflect the provider's health; a 400/401/404 is our bug or config and must not trip the breaker.</summary>
    private static bool IsProviderProblem(AnthropicApiException ex) =>
        ex is AnthropicRateLimitException or Anthropic5xxException || (int)ex.StatusCode is 408 or 409 or 429 or >= 500;

    public async IAsyncEnumerable<LlmEvent> StreamAsync(
        LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parts = (string.IsNullOrWhiteSpace(_options.RefusalFallbackModel)
            ? OpenPlainStream(request, streamCts.Token)
            : OpenFallbackStream(request, streamCts.Token)).GetAsyncEnumerator(streamCts.Token);
        var enumerator = parts;

        try
        {
            // Guard only the start of the stream (connect, headers, first event) with the breaker and timeout.
            bool hasFirst;
            try
            {
                hasFirst = await _pipeline.ExecuteAsync(async ct =>
                {
                    await using var link = ct.Register(streamCts.Cancel);
                    return await enumerator.MoveNextAsync();
                }, cancellationToken);
            }
            catch (BrokenCircuitException ex)
            {
                throw new LlmUnavailableException("The assistant is temporarily unavailable. Please try again shortly.", ex);
            }
            catch (TimeoutRejectedException ex)
            {
                throw new LlmUnavailableException("The assistant took too long to respond. Please try again.", ex);
            }
            catch (AnthropicApiException ex) when (IsProviderProblem(ex))
            {
                throw new LlmUnavailableException("The assistant is unavailable or busy right now. Please try again shortly.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new LlmUnavailableException("Could not reach the assistant. Please try again shortly.", ex);
            }

            var model = _options.Model;
            var inputTokens = 0;
            var outputTokens = 0;
            var stopReason = "end_turn";

            for (var more = hasFirst; more; more = await enumerator.MoveNextAsync())
            {
                var part = enumerator.Current;
                switch (part.Kind)
                {
                    case PartKind.Start:
                        model = part.Model ?? model;
                        inputTokens = part.Tokens;
                        break;
                    case PartKind.Text:
                        yield return new LlmTextDelta(part.Text!);
                        break;
                    case PartKind.End:
                        outputTokens = part.Tokens;
                        stopReason = part.StopReason ?? stopReason;
                        break;
                }
            }

            yield return new LlmCompleted(model, inputTokens, outputTokens, stopReason);
        }
        finally
        {
            await streamCts.CancelAsync(); // abandoning the stream (client left) cancels the underlying HTTP call
            await enumerator.DisposeAsync();
        }
    }

    private enum PartKind
    {
        Start,
        Text,
        End,
    }

    /// <summary>The few facts we need from a stream event, independent of which SDK endpoint produced it.</summary>
    private readonly record struct StreamPart(PartKind Kind, string? Text = null, string? Model = null, int Tokens = 0, string? StopReason = null);

    private IAsyncEnumerable<StreamPart> OpenPlainStream(LlmRequest request, CancellationToken ct) => PlainParts(request, ct);

    private async IAsyncEnumerable<StreamPart> PlainParts(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = request.MaxTokens,
            System = request.System,
            Messages = request.Messages
                .Select(m => m.Role == LlmRole.User
                    ? (MessageParam)new UserMessageParam { Content = m.Content }
                    : new AssistantMessageParam { Content = m.Content })
                .ToList(),
            OutputConfig = new OutputConfig { Effort = ParseEffort(_options.Effort) },
        };

        await foreach (var streamEvent in _client.Messages.CreateStreaming(parameters, ct).WithCancellation(ct))
        {
            if (streamEvent.TryPickStart(out var start))
            {
                yield return new StreamPart(PartKind.Start, Model: start.Message.Model.Raw(), Tokens: (int)start.Message.Usage.InputTokens);
            }
            else if (streamEvent.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var text))
            {
                yield return new StreamPart(PartKind.Text, Text: text.Text);
            }
            else if (streamEvent.TryPickDelta(out var delta))
            {
                yield return new StreamPart(PartKind.End, Tokens: (int)delta.Usage.OutputTokens, StopReason: delta.Delta.StopReason?.Raw());
            }
        }
    }

    /// <summary>
    /// The same request on the beta endpoint with server-side refusal fallbacks: if the primary model's safety
    /// classifiers decline a request, it is transparently re-served by the fallback model inside the same call
    /// (Claude API guidance for this model family). A refusal that survives the fallback still reaches the caller as
    /// stop reason "refusal". Disable by clearing Llm:Anthropic:RefusalFallbackModel.
    /// </summary>
    private async IAsyncEnumerable<StreamPart> OpenFallbackStream(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var parameters = new Beta.MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = request.MaxTokens,
            System = request.System,
            Messages = request.Messages
                .Select(m => m.Role == LlmRole.User
                    ? (Beta.BetaMessageParam)new Beta.BetaUserMessageParam { Content = m.Content }
                    : new Beta.BetaAssistantMessageParam { Content = m.Content })
                .ToList(),
            OutputConfig = new Beta.BetaOutputConfig { Effort = ParseBetaEffort(_options.Effort) },
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new([new(_options.RefusalFallbackModel!)]),
        };

        await foreach (var streamEvent in _client.Beta.Messages.CreateStreaming(parameters, ct).WithCancellation(ct))
        {
            if (streamEvent.TryPickStart(out var start))
            {
                yield return new StreamPart(PartKind.Start, Model: start.Message.Model.Raw(), Tokens: (int)start.Message.Usage.InputTokens);
            }
            else if (streamEvent.TryPickContentBlockDelta(out var block) && block.Delta.TryPickText(out var text))
            {
                yield return new StreamPart(PartKind.Text, Text: text.Text);
            }
            else if (streamEvent.TryPickDelta(out var delta))
            {
                yield return new StreamPart(PartKind.End, Tokens: (int)delta.Usage.OutputTokens, StopReason: delta.Delta.StopReason?.Raw());
            }
        }
    }

    private static Beta.Effort ParseBetaEffort(string value) => ParseEffort(value) switch
    {
        Effort.Low => Beta.Effort.Low,
        Effort.Medium => Beta.Effort.Medium,
        Effort.High => Beta.Effort.High,
        _ => Beta.Effort.Max,
    };

    private static Effort ParseEffort(string value) => value.ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "medium" => Effort.Medium,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => throw new ArgumentException($"Unknown Llm:Anthropic:Effort '{value}'. Use low, medium, high or max."),
    };
}
