using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Application.Abstractions;
using Application.Chat;
using Application.Ingestion;
using Domain;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Chat;

/// <summary>What the chat service streams back to the caller.</summary>
public abstract record ChatEvent;

/// <summary>The passages the answer is based on, numbered as the model will cite them.</summary>
public sealed record SourcesEvent(IReadOnlyList<CitationDto> Sources) : ChatEvent;

public sealed record DeltaEvent(string Text) : ChatEvent;

public sealed record DoneEvent(
    Guid MessageId,
    string Content,
    IReadOnlyList<CitationDto> Citations,
    string Model,
    string PromptVersion,
    bool FromCache,
    bool Grounded,
    int InputTokens,
    int OutputTokens) : ChatEvent;

/// <summary>
/// Orchestrates one question: save it, embed it, try the semantic cache, retrieve permitted passages, build a
/// budgeted prompt, stream the model's answer, extract citations, save the answer, and cache it when safe.
/// Cancellation (the user pressing Stop or closing the tab) flows from the request all the way to the model call.
/// </summary>
public sealed partial class ChatService(
    AppDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IEmbeddingClient embeddings,
    IRetrievalService retrieval,
    ISemanticCache cache,
    ILlmClient llm,
    IOptions<ChatOptions> options,
    ILogger<ChatService> logger)
{
    public const string NoSourcesAnswer =
        "I couldn't find anything relevant to that in the documents you have access to.";

    public const string RefusalAnswer =
        "I can't help with that request.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<ChatEvent> AskAsync(
        Conversation conversation, string question, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options.Value;
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant for chat request.");
        var userId = user.UserId ?? throw new InvalidOperationException("No user for chat request.");
        question = question.Trim();

        // 1. Remember what was said so far, then record the new question.
        var history = await LoadHistoryAsync(conversation.Id, ct);
        var firstTurn = history.Count == 0;

        db.ChatMessages.Add(new ChatMessage(tenantId, conversation.Id, ChatRole.User, question));
        if (firstTurn)
        {
            conversation.Rename(Truncate(question, 60));
        }

        conversation.Touch();
        await db.SaveChangesAsync(ct);

        // 2. Embed the question (used for both the cache and retrieval).
        var embedding = (await embeddings.EmbedAsync([question], ct))[0];
        var scope = $"{conversation.WorkspaceId?.ToString("N") ?? "all"}:{SafeKey(embeddings.ModelId)}";

        // 3. Semantic cache: only for a conversation's first question, whose answer depends on nothing but the
        //    question and the documents (a follow-up like "and its price?" means something different each time).
        if (o.CacheEnabled && firstTurn)
        {
            var hit = await TryCacheAsync(tenantId, userId, scope, embedding, ct);
            if (hit is not null)
            {
                yield return new SourcesEvent(hit.Citations);
                foreach (var piece in SplitForStreaming(hit.Answer))
                {
                    yield return new DeltaEvent(piece);
                }

                var cachedId = await SaveAssistantAsync(
                    conversation, tenantId, hit.Answer, hit.Citations, hit.Model, hit.PromptVersion, fromCache: true, 0, 0, ct);
                yield return new DoneEvent(cachedId, hit.Answer, hit.Citations, hit.Model, hit.PromptVersion, true, true, 0, 0);
                yield break;
            }
        }

        // 4. Retrieve passages the user is allowed to read. (Permissions are enforced inside the SQL.)
        var chunks = await retrieval.SearchAsync(
            new RetrievalQuery(
                tenantId, userId, user.IsAdmin, conversation.WorkspaceId, question, embedding,
                embeddings.ModelId, o.TopK, o.MinSimilarity),
            ct);

        if (chunks.Count == 0)
        {
            // Nothing relevant: say so without spending a model call (and without inviting a guess).
            yield return new SourcesEvent([]);
            yield return new DeltaEvent(NoSourcesAnswer);
            var noneId = await SaveAssistantAsync(
                conversation, tenantId, NoSourcesAnswer, [], "none", PromptTemplates.RagV1, fromCache: false, 0, 0, ct);
            yield return new DoneEvent(noneId, NoSourcesAnswer, [], "none", PromptTemplates.RagV1, false, false, 0, 0);
            yield break;
        }

        // 5. Fit passages and history into their token budgets, number the sources, build the prompt.
        var fitted = PromptBuilder.FitToBudget(chunks, c => TokenBudget.Estimate(c.Content) + 30, o.ContextTokenBudget);
        var sources = fitted
            .Select((c, i) => new PromptSource(i + 1, c.DocumentId, c.WorkspaceId, c.DocumentTitle, c.ChunkIndex, c.Content))
            .ToList();
        var allSources = ToSourceCitations(sources);
        yield return new SourcesEvent(allSources);

        var trimmedHistory = PromptBuilder.TrimHistory(
            history.Select(m => new LlmMessage(m.Role == ChatRole.User ? LlmRole.User : LlmRole.Assistant, m.Content)).ToList(),
            o.HistoryTokenBudget);
        var prompt = PromptBuilder.Build(question, sources, trimmedHistory, o.MaxAnswerTokens);

        // 6. Stream the answer.
        var answer = new StringBuilder();
        LlmCompleted? completed = null;
        await foreach (var llmEvent in llm.StreamAsync(prompt.Request, ct))
        {
            switch (llmEvent)
            {
                case LlmTextDelta delta:
                    answer.Append(delta.Text);
                    yield return new DeltaEvent(delta.Text);
                    break;
                case LlmCompleted done:
                    completed = done;
                    break;
            }
        }

        var stopReason = completed?.StopReason ?? "end_turn";
        var refused = stopReason == "refusal";
        if (refused && answer.Length == 0)
        {
            answer.Append(RefusalAnswer);
            yield return new DeltaEvent(RefusalAnswer);
        }

        // 7. Keep only citations that point at real sources; the model's [n] markers are untrusted.
        var text = answer.ToString();
        var citations = CitationExtractor.Extract(text, sources);
        var grounded = citations.Count > 0 && !refused;
        var model = completed?.Model ?? llm.ModelId;

        // 8. Save, then remember the answer for next time when it is safe to reuse.
        var messageId = await SaveAssistantAsync(
            conversation, tenantId, text, citations, model, prompt.PromptVersion, fromCache: false,
            completed?.InputTokens, completed?.OutputTokens, ct);

        if (o.CacheEnabled && firstTurn && grounded && stopReason == "end_turn")
        {
            await TryStoreCacheAsync(
                tenantId, scope, embedding,
                new CachedAnswer(question, text, citations, sources.Select(s => s.DocumentId).Distinct().ToList(), model, prompt.PromptVersion),
                ct);
        }

        yield return new DoneEvent(
            messageId, text, citations, model, prompt.PromptVersion, false, grounded,
            completed?.InputTokens ?? 0, completed?.OutputTokens ?? 0);
    }

    private static List<CitationDto> ToSourceCitations(IReadOnlyList<PromptSource> sources) =>
        sources.Select(s => new CitationDto(
            s.Number, s.DocumentId, s.WorkspaceId, s.DocumentTitle, s.ChunkIndex,
            s.Content.Length <= 240 ? s.Content : s.Content[..240].TrimEnd() + "…")).ToList();

    private async Task<List<ChatMessage>> LoadHistoryAsync(Guid conversationId, CancellationToken ct) =>
        await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

    private async Task<Guid> SaveAssistantAsync(
        Conversation conversation,
        Guid tenantId,
        string text,
        IReadOnlyList<CitationDto> citations,
        string model,
        string promptVersion,
        bool fromCache,
        int? inputTokens,
        int? outputTokens,
        CancellationToken ct)
    {
        var message = new ChatMessage(tenantId, conversation.Id, ChatRole.Assistant, text);
        message.CompleteAssistant(
            citations.Count == 0 ? null : JsonSerializer.Serialize(citations, Json),
            model, promptVersion, fromCache, inputTokens, outputTokens);
        db.ChatMessages.Add(message);
        conversation.Touch();
        await db.SaveChangesAsync(ct);
        return message.Id;
    }

    /// <summary>A cached answer, offered only if the asking user may read every document it was built from.</summary>
    private async Task<CachedAnswer?> TryCacheAsync(Guid tenantId, string userId, string scope, float[] embedding, CancellationToken ct)
    {
        try
        {
            var candidate = await cache.FindAsync(tenantId, scope, embedding, ct);
            if (candidate is null)
            {
                return null;
            }

            // Another user (with wider access) may have produced this answer. Reusing it for someone who cannot
            // read its sources would leak their content, so check against *this* user's permissions now.
            var allowed = await retrieval.CanReadAllAsync(tenantId, userId, user.IsAdmin, candidate.SourceDocumentIds, ct);
            return allowed ? candidate : null;
        }
#pragma warning disable CA1031 // The cache is an optimisation: any failure means "miss", never "error".
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogCacheError(logger, ex);
            return null;
        }
    }

    private async Task TryStoreCacheAsync(Guid tenantId, string scope, float[] embedding, CachedAnswer answer, CancellationToken ct)
    {
        try
        {
            await cache.StoreAsync(tenantId, scope, embedding, answer, ct);
        }
#pragma warning disable CA1031 // See above: caching is best-effort.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogCacheError(logger, ex);
        }
    }

    /// <summary>Replays a stored answer in small pieces so it streams like a live one.</summary>
    private static IEnumerable<string> SplitForStreaming(string text)
    {
        var words = text.Split(' ');
        for (var i = 0; i < words.Length; i += 6)
        {
            var piece = string.Join(' ', words.Skip(i).Take(6));
            yield return i + 6 < words.Length ? piece + " " : piece;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd() + "…";

    private static string SafeKey(string value) =>
        new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic cache unavailable; continuing without it")]
    private static partial void LogCacheError(ILogger logger, Exception exception);
}
