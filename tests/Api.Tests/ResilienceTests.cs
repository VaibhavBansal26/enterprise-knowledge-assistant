using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Application.Chat;
using Application.Ingestion;
using Domain;
using Infrastructure;
using Infrastructure.Llm;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;

namespace Api.Tests;

/// <summary>
/// A local server that speaks Anthropic's streaming wire format, so the official SDK client (and our resilience
/// around it) is exercised against realistic responses, rate limits and outages without any network or API key.
/// </summary>
public sealed class AnthropicStub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<string> _bodies = [];
    private readonly List<Dictionary<string, string>> _headers = [];
    private int _requests;

    private AnthropicStub(WebApplication app, string baseUrl)
    {
        _app = app;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }

    public int Requests => Volatile.Read(ref _requests);

    public IReadOnlyList<string> Bodies
    {
        get
        {
            lock (_bodies)
            {
                return _bodies.ToList();
            }
        }
    }

    public IReadOnlyList<Dictionary<string, string>> Headers
    {
        get
        {
            lock (_headers)
            {
                return _headers.ToList();
            }
        }
    }

    /// <summary>Handles request number N (1-based). Return true if the stub wrote the response itself.</summary>
    public Func<int, HttpContext, Task<bool>>? Script { get; set; }

    public string[] Pieces { get; set; } = ["Hello", " from", " Claude"];

    public string StopReason { get; set; } = "end_turn";

    public static async Task<AnthropicStub> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        AnthropicStub? stub = null;
        app.MapPost("/v1/messages", async (HttpContext ctx) =>
        {
            var attempt = Interlocked.Increment(ref stub!._requests);
            using (var reader = new StreamReader(ctx.Request.Body))
            {
                var body = await reader.ReadToEndAsync();
                lock (stub._bodies)
                {
                    stub._bodies.Add(body);
                }
            }

            lock (stub._headers)
            {
                stub._headers.Add(ctx.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase));
            }

            if (stub.Script is { } script && await script(attempt, ctx))
            {
                return;
            }

            await stub.WriteSuccessAsync(ctx);
        });

        await app.StartAsync();
        var address = app.Urls.First();
        stub = new AnthropicStub(app, address);
        return stub;
    }

    public static async Task WriteErrorAsync(HttpContext ctx, int status, string type = "api_error", string? retryAfter = null)
    {
        ctx.Response.StatusCode = status;
        if (retryAfter is not null)
        {
            ctx.Response.Headers["retry-after"] = retryAfter;
        }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { type = "error", error = new { type, message = $"stub {status}" } }));
    }

    private async Task WriteSuccessAsync(HttpContext ctx)
    {
        ctx.Response.ContentType = "text/event-stream";
        async Task Send(string name, string json)
        {
            await ctx.Response.WriteAsync($"event: {name}\ndata: {json}\n\n", ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }

        await Send("message_start", """{"type":"message_start","message":{"id":"msg_stub","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":25,"output_tokens":1}}}""");
        await Send("content_block_start", """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""");
        foreach (var piece in Pieces)
        {
            await Send("content_block_delta", JsonSerializer.Serialize(new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "text_delta", text = piece },
            }));
        }

        await Send("content_block_stop", """{"type":"content_block_stop","index":0}""");
        await Send("message_delta", JsonSerializer.Serialize(new
        {
            type = "message_delta",
            delta = new { stop_reason = StopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = 15 },
        }));
        await Send("message_stop", """{"type":"message_stop"}""");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed class AnthropicClientTests
{
    private static LlmRequest Request(string text = "What is the leave policy?") =>
        new("You are helpful.", [new LlmMessage(LlmRole.User, "Earlier question"), new LlmMessage(LlmRole.Assistant, "Earlier answer"), new LlmMessage(LlmRole.User, text)], 512);

    private static AnthropicLlmClient Client(AnthropicStub stub, Func<AnthropicOptions, AnthropicOptions>? tune = null)
    {
        var options = new AnthropicOptions
        {
            ApiKey = "sk-test-key",
            BaseUrl = stub.BaseUrl,
            MaxRetries = 0,
            FirstEventTimeoutSeconds = 5,
            RequestTimeoutSeconds = 30,
        };
        return new AnthropicLlmClient(Options.Create(new LlmOptions
        {
            Provider = LlmOptions.AnthropicProvider,
            Anthropic = tune?.Invoke(options) ?? options,
        }));
    }

    private static async Task<List<LlmEvent>> CollectAsync(AnthropicLlmClient client, LlmRequest request, CancellationToken ct = default)
    {
        var events = new List<LlmEvent>();
        await foreach (var e in client.StreamAsync(request, ct))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task Streams_text_and_reports_model_usage_and_stop_reason()
    {
        await using var stub = await AnthropicStub.StartAsync();
        using var client = Client(stub);

        var events = await CollectAsync(client, Request());

        Assert.Equal(["Hello", " from", " Claude"], events.OfType<LlmTextDelta>().Select(d => d.Text));
        var done = Assert.IsType<LlmCompleted>(events[^1]);
        Assert.Equal("claude-opus-5-5", done.Model);
        Assert.Equal(25, done.InputTokens);
        Assert.Equal(15, done.OutputTokens);
        Assert.Equal("end_turn", done.StopReason);
    }

    [Fact]
    public async Task Sends_the_documented_request_shape_with_the_key_in_the_authorization_header()
    {
        await using var stub = await AnthropicStub.StartAsync();
        using var client = Client(stub, o => new AnthropicOptions
        {
            ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, MaxRetries = 0, Effort = "medium", FirstEventTimeoutSeconds = 5,
        });

        await CollectAsync(client, Request("hello"));

        using var body = JsonDocument.Parse(Assert.Single(stub.Bodies));
        var root = body.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("You are helpful.", root.GetProperty("system").GetString());
        Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        // The official SDK authenticates with a bearer token; the key must never appear in the URL or body.
        Assert.Equal("Bearer sk-test-key", Assert.Single(stub.Headers)["Authorization"]);
        Assert.DoesNotContain("sk-test-key", Assert.Single(stub.Bodies), StringComparison.Ordinal);
        // Assistant "prefill" is not supported by current models: the conversation must end with a user turn.
        Assert.Equal("user", messages[^1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task Server_side_refusal_fallback_is_requested_by_default()
    {
        await using var stub = await AnthropicStub.StartAsync();
        using var client = Client(stub);

        await CollectAsync(client, Request());

        Assert.Contains("server-side-fallback-2026-07-01", Assert.Single(stub.Headers)["anthropic-beta"], StringComparison.Ordinal);
        using var body = JsonDocument.Parse(Assert.Single(stub.Bodies));
        var fallback = Assert.Single(body.RootElement.GetProperty("fallbacks").EnumerateArray());
        Assert.Equal("claude-opus-4-8", fallback.GetProperty("model").GetString());
        Assert.Equal("claude-opus-5-5", body.RootElement.GetProperty("model").GetString()); // the primary model is unchanged
    }

    [Fact]
    public async Task Clearing_the_fallback_model_turns_the_feature_off()
    {
        await using var stub = await AnthropicStub.StartAsync();
        using var client = Client(stub, o => new AnthropicOptions
        {
            ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, MaxRetries = 0, FirstEventTimeoutSeconds = 5, RefusalFallbackModel = "",
        });

        await CollectAsync(client, Request());

        Assert.False(Assert.Single(stub.Headers).TryGetValue("anthropic-beta", out var beta) && beta.Contains("fallback", StringComparison.Ordinal));
        using var body = JsonDocument.Parse(Assert.Single(stub.Bodies));
        Assert.False(body.RootElement.TryGetProperty("fallbacks", out _));
    }

    [Fact]
    public async Task A_refusal_stop_reason_is_passed_through_for_the_caller_to_handle()
    {
        await using var stub = await AnthropicStub.StartAsync();
        stub.Pieces = [];
        stub.StopReason = "refusal";
        using var client = Client(stub);

        var events = await CollectAsync(client, Request());

        Assert.Equal("refusal", Assert.IsType<LlmCompleted>(events[^1]).StopReason);
    }

    [Fact]
    public async Task Rate_limits_are_retried_after_the_provider_requested_delay()
    {
        await using var stub = await AnthropicStub.StartAsync();
        stub.Script = async (attempt, ctx) =>
        {
            if (attempt <= 2)
            {
                await AnthropicStub.WriteErrorAsync(ctx, 429, "rate_limit_error", retryAfter: "0");
                return true;
            }

            return false;
        };
        using var client = Client(stub, o => new AnthropicOptions { ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, MaxRetries = 3, FirstEventTimeoutSeconds = 10 });

        var events = await CollectAsync(client, Request());

        Assert.Equal(3, stub.Requests); // two 429s, then success
        Assert.Equal("Hello from Claude", string.Concat(events.OfType<LlmTextDelta>().Select(d => d.Text)));
    }

    [Fact]
    public async Task Server_errors_are_retried_and_a_persistent_outage_becomes_a_clean_unavailable_error()
    {
        await using var stub = await AnthropicStub.StartAsync();
        var failFirst = 1;
        stub.Script = async (attempt, ctx) =>
        {
            if (attempt <= failFirst)
            {
                await AnthropicStub.WriteErrorAsync(ctx, 503, "overloaded_error", retryAfter: "0");
                return true;
            }

            return false;
        };
        using var client = Client(stub, o => new AnthropicOptions { ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, MaxRetries = 2, FirstEventTimeoutSeconds = 10 });

        await CollectAsync(client, Request()); // one 503 then success
        Assert.Equal(2, stub.Requests);

        failFirst = int.MaxValue; // now the provider is down for good
        var before = stub.Requests;
        await Assert.ThrowsAsync<LlmUnavailableException>(() => CollectAsync(client, Request()));
        Assert.Equal(before + 3, stub.Requests); // the original try plus two retries, then give up
    }

    [Fact]
    public async Task Client_errors_are_not_retried_and_do_not_count_against_the_circuit_breaker()
    {
        await using var stub = await AnthropicStub.StartAsync();
        stub.Script = async (_, ctx) =>
        {
            await AnthropicStub.WriteErrorAsync(ctx, 401, "authentication_error");
            return true;
        };
        using var client = Client(stub, o => new AnthropicOptions
        {
            ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, MaxRetries = 3, FirstEventTimeoutSeconds = 5, BreakerMinimumThroughput = 2, BreakerBreakSeconds = 30,
        });

        for (var i = 0; i < 4; i++)
        {
            var ex = await Record.ExceptionAsync(() => CollectAsync(client, Request()));
            Assert.NotNull(ex);
            Assert.IsNotType<LlmUnavailableException>(ex); // a bad key is our configuration problem, not an outage
        }

        Assert.Equal(4, stub.Requests); // every call reached the server: the breaker never opened, nothing was retried
    }

    [Fact]
    public async Task The_circuit_breaker_opens_during_an_outage_fails_fast_then_recovers()
    {
        await using var stub = await AnthropicStub.StartAsync();
        var healthy = false;
        stub.Script = async (_, ctx) =>
        {
            if (healthy)
            {
                return false;
            }

            await AnthropicStub.WriteErrorAsync(ctx, 500);
            return true;
        };
        using var client = Client(stub, o => new AnthropicOptions
        {
            ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, MaxRetries = 0, FirstEventTimeoutSeconds = 5,
            BreakerMinimumThroughput = 3, BreakerFailureRatio = 0.5, BreakerSamplingSeconds = 30, BreakerBreakSeconds = 1,
        });

        for (var i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<LlmUnavailableException>(() => CollectAsync(client, Request()));
        }

        Assert.Equal(3, stub.Requests);

        // Open: calls fail immediately and never reach the provider.
        var fast = await Assert.ThrowsAsync<LlmUnavailableException>(() => CollectAsync(client, Request()));
        Assert.IsType<BrokenCircuitException>(fast.InnerException);
        Assert.Equal(3, stub.Requests);

        // After the break the provider has recovered: a probe succeeds and the circuit closes.
        healthy = true;
        await Task.Delay(1300);
        var events = await CollectAsync(client, Request());
        Assert.Equal("Hello from Claude", string.Concat(events.OfType<LlmTextDelta>().Select(d => d.Text)));
        Assert.Equal(4, stub.Requests);
    }

    [Fact]
    public async Task A_provider_that_never_starts_responding_times_out_instead_of_hanging()
    {
        await using var stub = await AnthropicStub.StartAsync();
        stub.Script = async (_, ctx) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ctx.RequestAborted);
            return false;
        };
        using var client = Client(stub, o => new AnthropicOptions { ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, MaxRetries = 0, FirstEventTimeoutSeconds = 1 });

        var started = DateTime.UtcNow;
        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => CollectAsync(client, Request()));

        Assert.Contains("too long", ex.Message, StringComparison.Ordinal);
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 0.8, 5);
    }

    [Fact]
    public async Task Cancelling_the_caller_aborts_the_request_to_the_provider()
    {
        await using var stub = await AnthropicStub.StartAsync();
        var providerSawAbort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stub.Script = async (_, ctx) =>
        {
            ctx.RequestAborted.Register(() => providerSawAbort.TrySetResult());
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-opus-5-5\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n");
            await ctx.Response.Body.FlushAsync();
            await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ConfigureAwait(false);
            return true;
        };
        using var client = Client(stub);
        using var cts = new CancellationTokenSource();

        var stream = CollectAsync(client, Request(), cts.Token);
        await Task.Delay(500);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream);
        await providerSawAbort.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the HTTP call to the provider was torn down
    }
}

public sealed class EmbeddingClientResilienceTests
{
    private sealed class ScriptedHandler(Func<int, HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public List<string> Bodies { get; } = [];

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _calls);
            LastRequest = request;
            if (request.Content is not null)
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                lock (Bodies)
                {
                    Bodies.Add(body);
                }
            }

            return await respond(attempt, request).WaitAsync(cancellationToken);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json, string? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    private static string VectorsFor(int count, int dims = DocumentChunk.EmbeddingDimensions, bool reversed = false)
    {
        var items = Enumerable.Range(0, count).Select(i => new
        {
            index = i,
            embedding = Enumerable.Range(0, dims).Select(d => d == i ? 1f : 0f).ToArray(),
        });
        if (reversed)
        {
            items = items.Reverse();
        }

        return JsonSerializer.Serialize(new { data = items });
    }

    private static (OpenAiCompatibleEmbeddingClient Client, ServiceProvider Provider) Build(
        ScriptedHandler handler, Func<OpenAiEmbeddingOptions, OpenAiEmbeddingOptions>? tune = null)
    {
        var settings = new OpenAiEmbeddingOptions
        {
            ApiKey = "sk-embed", BaseUrl = "https://embeddings.test/v1/", BatchSize = 2,
            MaxRetryAttempts = 2, RetryDelayMs = 10, AttemptTimeoutSeconds = 1, TotalTimeoutSeconds = 20,
            BreakSeconds = 1, BreakerMinimumThroughput = 3, BreakerFailureRatio = 0.5,
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new EmbeddingsOptions
        {
            Provider = EmbeddingsOptions.OpenAiProvider,
            OpenAi = tune?.Invoke(settings) ?? settings,
        }));
        services.AddOpenAiCompatibleEmbeddings().ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<OpenAiCompatibleEmbeddingClient>(), provider);
    }

    [Fact]
    public async Task Sends_model_dimensions_and_key_and_returns_vectors_in_input_order()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, VectorsFor(2, reversed: true))));
        var (client, provider) = Build(handler);
        using var _ = provider;

        var vectors = await client.EmbedAsync(["alpha", "beta"], CancellationToken.None);

        Assert.Equal(2, vectors.Count);
        Assert.Equal(1f, vectors[0][0]); // index 0 first, even though the provider listed it last
        Assert.Equal(1f, vectors[1][1]);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("text-embedding-3-small", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(DocumentChunk.EmbeddingDimensions, body.RootElement.GetProperty("dimensions").GetInt32());
        Assert.Equal(["alpha", "beta"], body.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Bearer sk-embed", handler.LastRequest!.Headers.Authorization!.ToString());
        Assert.Equal("openai:text-embedding-3-small:384", client.ModelId);
    }

    [Fact]
    public async Task Large_inputs_are_sent_in_batches()
    {
        var handler = new ScriptedHandler((_, req) =>
        {
            var inputs = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement.GetProperty("input").GetArrayLength();
            return Task.FromResult(Json(HttpStatusCode.OK, VectorsFor(inputs)));
        });
        var (client, provider) = Build(handler);
        using var _ = provider;

        var vectors = await client.EmbedAsync(["a", "b", "c", "d", "e"], CancellationToken.None);

        Assert.Equal(5, vectors.Count);
        Assert.Equal(3, handler.Calls); // 2 + 2 + 1
    }

    [Fact]
    public async Task Rate_limiting_is_retried_honouring_retry_after()
    {
        var handler = new ScriptedHandler((attempt, _) => Task.FromResult(attempt == 1
            ? Json(HttpStatusCode.TooManyRequests, "{}", retryAfter: "0")
            : Json(HttpStatusCode.OK, VectorsFor(1))));
        var (client, provider) = Build(handler);
        using var _ = provider;

        var vectors = await client.EmbedAsync(["alpha"], CancellationToken.None);

        Assert.Single(vectors);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Transient_server_errors_are_retried_with_a_limit()
    {
        var failures = 2;
        var handler = new ScriptedHandler((attempt, _) => Task.FromResult(attempt <= failures
            ? Json(HttpStatusCode.ServiceUnavailable, "{}")
            : Json(HttpStatusCode.OK, VectorsFor(1))));
        var (client, provider) = Build(handler, o => new OpenAiEmbeddingOptions
        {
            ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, BatchSize = 2, MaxRetryAttempts = 2, RetryDelayMs = 10,
            AttemptTimeoutSeconds = 1, TotalTimeoutSeconds = 20, BreakSeconds = 1, BreakerMinimumThroughput = 1000,
        });
        using var _ = provider;

        await client.EmbedAsync(["alpha"], CancellationToken.None);
        Assert.Equal(3, handler.Calls); // two failures then success (MaxRetryAttempts = 2)

        failures = int.MaxValue;
        var before = handler.Calls;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(["alpha"], CancellationToken.None));
        Assert.Equal(before + 3, handler.Calls); // 1 + 2 retries, then it surfaces
    }

    [Fact]
    public async Task A_rejected_key_fails_permanently_without_retrying()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(Json(HttpStatusCode.Unauthorized, "{}")));
        var (client, provider) = Build(handler);
        using var _ = provider;

        var ex = await Assert.ThrowsAsync<IngestionException>(() => client.EmbedAsync(["alpha"], CancellationToken.None));

        Assert.True(ex.IsPermanent);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Vectors_of_the_wrong_size_are_a_permanent_configuration_error()
    {
        var handler = new ScriptedHandler((_, _) => Task.FromResult(Json(HttpStatusCode.OK, VectorsFor(1, dims: 1536))));
        var (client, provider) = Build(handler);
        using var _ = provider;

        var ex = await Assert.ThrowsAsync<IngestionException>(() => client.EmbedAsync(["alpha"], CancellationToken.None));

        Assert.True(ex.IsPermanent);
        Assert.Contains("dimensions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_slow_attempt_is_timed_out_and_retried()
    {
        var handler = new ScriptedHandler(async (attempt, _) =>
        {
            if (attempt == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(10)); // exceeds the 1s per-attempt timeout
            }

            return Json(HttpStatusCode.OK, VectorsFor(1));
        });
        var (client, provider) = Build(handler);
        using var _ = provider;

        var vectors = await client.EmbedAsync(["alpha"], CancellationToken.None);

        Assert.Single(vectors);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task The_circuit_breaker_stops_hammering_a_failing_provider_then_recovers()
    {
        var healthy = false;
        var handler = new ScriptedHandler((_, _) => Task.FromResult(healthy
            ? Json(HttpStatusCode.OK, VectorsFor(1))
            : Json(HttpStatusCode.InternalServerError, "{}")));
        var (client, provider) = Build(handler, o => new OpenAiEmbeddingOptions
        {
            ApiKey = o.ApiKey, BaseUrl = o.BaseUrl, BatchSize = 2, MaxRetryAttempts = 1, RetryDelayMs = 10,
            AttemptTimeoutSeconds = 1, TotalTimeoutSeconds = 20, BreakSeconds = 1, BreakerMinimumThroughput = 4, BreakerFailureRatio = 0.5,
        });
        using var _ = provider;

        // Two calls, each with one retry = four failed attempts: enough to open the circuit.
        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync(["alpha"], CancellationToken.None));
        }

        var callsWhenOpened = handler.Calls;
        Assert.Equal(4, callsWhenOpened);
        await Assert.ThrowsAsync<BrokenCircuitException>(() => client.EmbedAsync(["alpha"], CancellationToken.None));
        Assert.Equal(callsWhenOpened, handler.Calls); // failed fast: the provider was not called

        healthy = true;
        await Task.Delay(1300);
        Assert.Single(await client.EmbedAsync(["alpha"], CancellationToken.None));
    }
}
