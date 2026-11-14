using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Application.Chat;
using Application.Workspaces;
using Infrastructure.Ingestion;
using Infrastructure.Llm;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Api.Tests;

/// <summary>One parsed Server-Sent Event.</summary>
public sealed record SseEvent(string Name, JsonElement Data);

public static class Sse
{
    /// <summary>Reads a text/event-stream response to the end.</summary>
    public static async Task<List<SseEvent>> ReadAllAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        var events = new List<SseEvent>();
        await foreach (var e in StreamAsync(response, ct))
        {
            events.Add(e);
        }

        return events;
    }

    public static async IAsyncEnumerable<SseEvent> StreamAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? name = null;
        string? data = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                name = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line["data: ".Length..];
            }
            else if (line.Length == 0 && name is not null && data is not null)
            {
                yield return new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone());
                name = null;
                data = null;
            }
        }
    }
}

/// <summary>An LLM that records every request and can be told to misbehave, wrapping the offline extractive model.</summary>
public sealed class RecordingLlm : ILlmClient
{
    private readonly ExtractiveLlmClient _inner = new();
    private readonly List<LlmRequest> _requests = [];

    public IReadOnlyList<LlmRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public int Calls => Requests.Count;

    public string ModelId => _inner.ModelId;

    /// <summary>When set, replaces the model's behaviour for the next calls.</summary>
    public Func<LlmRequest, CancellationToken, IAsyncEnumerable<LlmEvent>>? Behaviour { get; set; }

    public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }

        return Behaviour is { } custom ? custom(request, cancellationToken) : _inner.StreamAsync(request, cancellationToken);
    }
}

/// <summary>An embedding client that reports a different model id, as if the provider had been switched.</summary>
public sealed class RenamedEmbeddingClient(string modelId) : Application.Ingestion.IEmbeddingClient
{
    private readonly HashingEmbeddingClient _inner = new();

    public string ModelId => modelId;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
        _inner.EmbedAsync(texts, cancellationToken);
}

public static class TestHosts
{
    /// <summary>Starts a host with the ingestion worker enabled, after clearing unfinished work left by other tests.</summary>
    public static async Task<WebApplicationFactory<Program>> StartWorkerHostAsync(
        ApiFactory factory, Action<IServiceCollection>? services = null, params (string Key, string Value)[] settings)
    {
        await using (var cmd = factory.Services.GetRequiredService<NpgsqlDataSource>()
            .CreateCommand("DELETE FROM outbox_messages WHERE processed_at IS NULL AND dead_at IS NULL"))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        return factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Ingestion:WorkerEnabled", "true");
            foreach (var (key, value) in settings)
            {
                b.UseSetting(key, value);
            }

            if (services is not null)
            {
                b.ConfigureTestServices(services);
            }
        });
    }

    public static Action<IServiceCollection> Llm(ILlmClient llm) => services =>
    {
        services.RemoveAll<ILlmClient>();
        services.AddSingleton(llm);
    };

    public static Action<IServiceCollection> Combine(params Action<IServiceCollection>[] actions) => services =>
    {
        foreach (var action in actions)
        {
            action(services);
        }
    };
}

public static class ChatHelpers
{
    public const string LeaveText =
        "Parental leave policy. Employees receive sixteen weeks of paid parental leave after the birth or adoption " +
        "of a child. The leave can be taken in one block or split into two blocks within the first year.";

    public const string LeaveQuestion = "How many weeks of paid parental leave do employees receive?";

    public static async Task<Guid> CreateWorkspaceAsync(HttpClient client, string? name = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest(name ?? $"chat-{Guid.NewGuid():N}", null));
        return (await response.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
    }

    /// <summary>Uploads a Markdown document and waits until it is searchable.</summary>
    public static async Task<Application.Documents.DocumentResponse> UploadReadyAsync(
        HttpClient client, Guid workspaceId, string title, string text)
    {
        var created = await TestFiles.UploadOkAsync(client, workspaceId, $"{Guid.NewGuid():N}.md", TestFiles.Markdown(text), title);
        return await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Ready");
    }

    public static async Task<Guid> CreateConversationAsync(HttpClient client, Guid? workspaceId = null)
    {
        var response = await client.PostAsJsonAsync("/api/v1/conversations", new CreateConversationRequest(workspaceId, null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConversationResponse>())!.Id;
    }

    public static async Task<(HttpResponseMessage Response, List<SseEvent> Events)> AskAsync(
        HttpClient client, Guid conversationId, string question)
    {
        var response = await client.PostAsJsonAsync($"/api/v1/conversations/{conversationId}/messages", new AskRequest(question));
        var events = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? await Sse.ReadAllAsync(response)
            : [];
        return (response, events);
    }

    public static SseEvent Done(List<SseEvent> events) => Assert.Single(events, e => e.Name == "done");

    public static string Text(List<SseEvent> events) =>
        string.Concat(events.Where(e => e.Name == "delta").Select(e => e.Data.GetProperty("text").GetString()));
}
