using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Application.Abstractions;
using Application.Chat;
using Application.Ingestion;
using Application.Safety;
using Application.Telemetry;
using Domain;
using Infrastructure.Persistence;
using Infrastructure.Usage;
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
    int OutputTokens,
    double GroundingScore,
    bool LowGrounding,
    int InjectionNeutralized,
    int PiiRedactions) : ChatEvent;

/// <summary>
/// Orchestrates one question: mask personal data, save it, embed it, try the semantic cache, retrieve permitted
/// passages, strip instruction-like text from them, build a budgeted prompt, stream the model's answer while
/// watching for prompt leakage, extract citations, check grounding, save the answer and its usage, and cache the
/// answer when it is safe to reuse. Cancellation (the user pressing Stop) flows to the model call.
/// </summary>
public sealed partial class ChatService(
    AppDbContext db,
    ITenantContext tenant,
    ICurrentUser user,
    IEmbeddingClient embeddings,
    IRetrievalService retrieval,
    ISemanticCache cache,
    ILlmClient llm,
    UsageService usage,
    IOptions<ChatOptions> options,
    ILogger<ChatService> logger)
{
    public const string NoSourcesAnswer =
        "I couldn't find anything relevant to that in the documents you have access to.";

    public const string RefusalAnswer =
        "I can't help with that request.";

    public const string LeakBlockedAnswer =
        "I can't share my internal instructions, but I'm happy to answer questions about your documents.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<ChatEvent> AskAsync(
        Conversation conversation, string question, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options.Value;
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant for chat request.");
        var userId = user.UserId ?? throw new InvalidOperationException("No user for chat request.");
        var clock = Stopwatch.StartNew();

        // Spans carry ids, counts and lengths only: never the question, the answer or document text.
        using var root = AppTelemetry.Source.StartActivity("chat.ask");
        root?.SetTag("kb.conversation.id", conversation.Id);
        root?.SetTag("kb.workspace.scoped", conversation.WorkspaceId is not null);

        // 1. Mask personal data before the text is stored, embedded or sent to a third-party model.
        var original = question.Trim();
        var redaction = o.RedactPii ? PiiRedactor.Redact(original) : new RedactionResult(original, new Dictionary<string, int>());
        question = redaction.Text;
        foreach (var (kind, count) in redaction.ByKind)
        {
            AppTelemetry.PiiRedactions.Add(count, new KeyValuePair<string, object?>("kind", kind));
        }

        if (InjectionScanner.LooksLikeInjection(question))
        {
            root?.SetTag("kb.question.suspicious", true); // flagged for observability; the system prompt still governs the answer
        }

        // 2. Remember what was said so far, then record the new question.
        var history = await LoadHistoryAsync(conversation.Id, ct);
        var firstTurn = history.Count == 0;
        root?.SetTag("kb.first_turn", firstTurn);

        db.ChatMessages.Add(new ChatMessage(tenantId, conversation.Id, ChatRole.User, question));
        if (firstTurn)
        {
            conversation.Rename(Truncate(question, 60));
        }

        conversation.Touch();
        await db.SaveChangesAsync(ct);

        // 3. Embed the question (used for both the cache and retrieval).
        float[] embedding;
        using (var span = Child(root, "chat.embed"))
        {
            embedding = (await embeddings.EmbedAsync([question], ct))[0];
            span?.SetTag("kb.embedding.model", embeddings.ModelId);
        }

        var scope = $"{conversation.WorkspaceId?.ToString("N") ?? "all"}:{SafeKey(embeddings.ModelId)}";
        var embeddingTokens = TokenBudget.Estimate(question);

        // 4. Semantic cache: only for a conversation's first question, whose answer depends on nothing but the
        //    question and the documents (a follow-up like "and its price?" means something different each time).
        if (o.CacheEnabled && firstTurn)
        {
            CachedAnswer? hit;
            using (var span = Child(root, "chat.cache.lookup"))
            {
                hit = await TryCacheAsync(tenantId, userId, scope, embedding, ct);
                span?.SetTag("kb.cache.hit", hit is not null);
            }

            if (hit is not null)
            {
                yield return new SourcesEvent(hit.Citations);
                var ttft = (int)clock.ElapsedMilliseconds;
                foreach (var piece in SplitForStreaming(hit.Answer))
                {
                    yield return new DeltaEvent(piece);
                }

                var cachedId = await SaveAssistantAsync(
                    conversation, tenantId, userId, hit.Answer, hit.Citations, hit.Model, hit.PromptVersion, ChatOutcome.Cached,
                    0, 0, 1.0, false, embeddingTokens, (int)clock.ElapsedMilliseconds, ttft, 0, 0, redaction.Count, ct);
                Finish(root, ChatOutcome.Cached, clock, ttft);
                yield return new DoneEvent(cachedId, hit.Answer, hit.Citations, hit.Model, hit.PromptVersion, true, true, 0, 0, 1.0, false, 0, redaction.Count);
                yield break;
            }
        }

        // 5. Retrieve passages the user is allowed to read. (Permissions are enforced inside the SQL.)
        IReadOnlyList<RetrievedChunk> chunks;
        using (var span = Child(root, "chat.retrieve"))
        {
            var retrievalClock = Stopwatch.StartNew();
            chunks = await retrieval.SearchAsync(
                new RetrievalQuery(
                    tenantId, userId, user.IsAdmin, conversation.WorkspaceId, question, embedding,
                    embeddings.ModelId, o.TopK, o.MinSimilarity),
                ct);
            AppTelemetry.RetrievalDuration.Record(retrievalClock.Elapsed.TotalMilliseconds);
            AppTelemetry.RetrievedPassages.Record(chunks.Count);
            span?.SetTag("kb.retrieval.passages", chunks.Count);
        }

        if (chunks.Count == 0)
        {
            // Nothing relevant: say so without spending a model call (and without inviting a guess).
            yield return new SourcesEvent([]);
            var ttft = (int)clock.ElapsedMilliseconds;
            yield return new DeltaEvent(NoSourcesAnswer);
            var noneId = await SaveAssistantAsync(
                conversation, tenantId, userId, NoSourcesAnswer, [], "none", PromptTemplates.RagV1, ChatOutcome.NoSources,
                0, 0, null, false, embeddingTokens, (int)clock.ElapsedMilliseconds, ttft, 0, 0, redaction.Count, ct);
            Finish(root, ChatOutcome.NoSources, clock, ttft);
            yield return new DoneEvent(noneId, NoSourcesAnswer, [], "none", PromptTemplates.RagV1, false, false, 0, 0, 1.0, false, 0, redaction.Count);
            yield break;
        }

        // 6. Fit passages into the token budget and strip instruction-like sentences before the model sees them.
        var fitted = PromptBuilder.FitToBudget(chunks, c => TokenBudget.Estimate(c.Content) + 30, o.ContextTokenBudget);
        var neutralized = 0;
        var sources = new List<PromptSource>();
        foreach (var (c, i) in fitted.Select((c, i) => (c, i)))
        {
            var content = c.Content;
            if (o.NeutralizeInjection)
            {
                var (clean, removed) = InjectionScanner.Neutralize(content);
                content = clean;
                neutralized += removed;
            }

            sources.Add(new PromptSource(i + 1, c.DocumentId, c.WorkspaceId, c.DocumentTitle, c.ChunkIndex, content));
        }

        if (neutralized > 0)
        {
            AppTelemetry.InjectionNeutralized.Add(neutralized);
            LogInjectionNeutralized(logger, neutralized, conversation.Id);
        }

        yield return new SourcesEvent(ToSourceCitations(sources));

        var trimmedHistory = PromptBuilder.TrimHistory(
            history.Select(m => new LlmMessage(m.Role == ChatRole.User ? LlmRole.User : LlmRole.Assistant, m.Content)).ToList(),
            o.HistoryTokenBudget);
        var prompt = PromptBuilder.Build(question, sources, trimmedHistory, o.MaxAnswerTokens);

        // 7. Stream the answer, stopping the stream if it starts repeating the hidden instructions.
        var answer = new StringBuilder();
        LlmCompleted? completed = null;
        var leaked = false;
        int? firstToken = null;
        using (var span = Child(root, "chat.llm.stream"))
        {
            span?.SetTag("kb.llm.model", llm.ModelId);
            await foreach (var llmEvent in llm.StreamAsync(prompt.Request, ct))
            {
                if (llmEvent is LlmTextDelta delta)
                {
                    answer.Append(delta.Text);
                    firstToken ??= (int)clock.ElapsedMilliseconds;

                    if (SystemPromptLeakDetector.Leaks(Tail(answer, 600), prompt.Request.System))
                    {
                        leaked = true;
                        break; // abandoning the enumeration cancels the model call
                    }

                    yield return new DeltaEvent(delta.Text);
                }
                else if (llmEvent is LlmCompleted done)
                {
                    completed = done;
                }
            }

            span?.SetTag("kb.llm.input_tokens", completed?.InputTokens);
            span?.SetTag("kb.llm.output_tokens", completed?.OutputTokens);
            span?.SetTag("kb.llm.stop_reason", completed?.StopReason);
        }

        var stopReason = completed?.StopReason ?? "end_turn";
        var refused = stopReason == "refusal";
        string text;
        if (leaked)
        {
            AppTelemetry.LeakBlocked.Add(1);
            LogLeakBlocked(logger, conversation.Id);
            text = LeakBlockedAnswer;
        }
        else if (refused && answer.Length == 0)
        {
            text = RefusalAnswer;
            yield return new DeltaEvent(RefusalAnswer);
        }
        else
        {
            text = answer.ToString();
        }

        // 8. Keep only citations that point at real sources; check the answer stays within them.
        var citations = leaked ? [] : CitationExtractor.Extract(text, sources);
        var grounding = leaked || refused ? new GroundingResult(1.0, 0, 0) : GroundingChecker.Score(text, sources);
        var lowGrounding = o.MinGroundingScore > 0 && grounding.AssessedSentences > 0 && grounding.Score < o.MinGroundingScore;
        var grounded = citations.Count > 0 && !refused && !leaked && !lowGrounding;
        var model = completed?.Model ?? llm.ModelId;
        var outcome = refused || leaked ? ChatOutcome.Refused : ChatOutcome.Answered;
        AppTelemetry.GroundingScore.Record(grounding.Score);

        // 9. Save, then remember the answer for next time when it is safe to reuse.
        var messageId = await SaveAssistantAsync(
            conversation, tenantId, userId, text, citations, model, prompt.PromptVersion, outcome,
            completed?.InputTokens ?? 0, completed?.OutputTokens ?? 0, grounding.Score, lowGrounding, embeddingTokens,
            (int)clock.ElapsedMilliseconds, firstToken, sources.Count, neutralized, redaction.Count, ct);

        if (o.CacheEnabled && firstTurn && grounded && stopReason == "end_turn")
        {
            await TryStoreCacheAsync(
                tenantId, scope, embedding,
                new CachedAnswer(question, text, citations, sources.Select(s => s.DocumentId).Distinct().ToList(), model, prompt.PromptVersion),
                ct);
        }

        Finish(root, outcome, clock, firstToken);
        root?.SetTag("kb.grounding.score", grounding.Score);
        yield return new DoneEvent(
            messageId, text, citations, model, prompt.PromptVersion, false, grounded,
            completed?.InputTokens ?? 0, completed?.OutputTokens ?? 0, grounding.Score, lowGrounding, neutralized, redaction.Count);
    }

    /// <summary>
    /// Records a question that did not produce an answer (model outage, the user pressing Stop, quota refusal) so the
    /// dashboard's error rate and latency reflect reality, not just the successes.
    /// </summary>
    public async Task RecordFailureAsync(Conversation conversation, string outcome, int latencyMs)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("No tenant for chat request.");
        AppTelemetry.ChatRequests.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        AppTelemetry.ChatDuration.Record(latencyMs, new KeyValuePair<string, object?>("outcome", outcome));

        db.UsageLedger.Add(new UsageLedgerEntry(tenantId, user.UserId, UsageLedgerEntry.KindChat, "none", outcome, 0, 0, 0m)
            .WithChatDetails(conversation.Id, null, latencyMs, null, null, 0, 0, 0));
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
#pragma warning disable CA1031 // Bookkeeping about a failure must never mask the failure itself.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLedgerFailed(logger, ex);
        }
    }

    private static Activity? Child(Activity? parent, string name) =>
        parent is null ? AppTelemetry.Source.StartActivity(name) : AppTelemetry.Source.StartActivity(name, ActivityKind.Internal, parent.Context);

    private static void Finish(Activity? root, string outcome, Stopwatch clock, int? firstTokenMs)
    {
        var tag = new KeyValuePair<string, object?>("outcome", outcome);
        AppTelemetry.ChatRequests.Add(1, tag);
        AppTelemetry.ChatDuration.Record(clock.Elapsed.TotalMilliseconds, tag);
        if (firstTokenMs is { } ft)
        {
            AppTelemetry.TimeToFirstToken.Record(ft, tag);
        }

        root?.SetTag("kb.outcome", outcome);
    }

    private static string Tail(StringBuilder text, int max) =>
        text.Length <= max ? text.ToString() : text.ToString(text.Length - max, max);

    private static List<CitationDto> ToSourceCitations(IReadOnlyList<PromptSource> sources) =>
        sources.Select(s => new CitationDto(
            s.Number, s.DocumentId, s.WorkspaceId, s.DocumentTitle, s.ChunkIndex,
            s.Content.Length <= 240 ? s.Content : s.Content[..240].TrimEnd() + "…")).ToList();

    private async Task<List<ChatMessage>> LoadHistoryAsync(Guid conversationId, CancellationToken ct) =>
        await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

    /// <summary>Saves the answer together with its usage-ledger rows in one transaction.</summary>
    private async Task<Guid> SaveAssistantAsync(
        Conversation conversation,
        Guid tenantId,
        string userId,
        string text,
        IReadOnlyList<CitationDto> citations,
        string model,
        string promptVersion,
        string outcome,
        int inputTokens,
        int outputTokens,
        double? grounding,
        bool lowGrounding,
        int embeddingTokens,
        int latencyMs,
        int? firstTokenMs,
        int retrieved,
        int neutralized,
        int piiRedactions,
        CancellationToken ct)
    {
        var message = new ChatMessage(tenantId, conversation.Id, ChatRole.Assistant, text);
        message.CompleteAssistant(
            citations.Count == 0 ? null : JsonSerializer.Serialize(citations, Json),
            model, promptVersion, outcome == ChatOutcome.Cached, inputTokens, outputTokens, grounding, lowGrounding);
        db.ChatMessages.Add(message);
        conversation.Touch();

        var cost = usage.CostOf(model, inputTokens, outputTokens);
        db.UsageLedger.Add(new UsageLedgerEntry(tenantId, userId, UsageLedgerEntry.KindChat, model, outcome, inputTokens, outputTokens, cost)
            .WithChatDetails(conversation.Id, message.Id, latencyMs, firstTokenMs, grounding, retrieved, neutralized, piiRedactions));
        db.UsageLedger.Add(new UsageLedgerEntry(
            tenantId, userId, UsageLedgerEntry.KindEmbedding, embeddings.ModelId, outcome, embeddingTokens, 0,
            usage.CostOf(embeddings.ModelId, embeddingTokens, 0)).InConversation(conversation.Id, message.Id));

        await db.SaveChangesAsync(ct);

        if (inputTokens + outputTokens > 0)
        {
            AppTelemetry.LlmTokens.Add(inputTokens, new KeyValuePair<string, object?>("direction", "input"), new KeyValuePair<string, object?>("model", model));
            AppTelemetry.LlmTokens.Add(outputTokens, new KeyValuePair<string, object?>("direction", "output"), new KeyValuePair<string, object?>("model", model));
            AppTelemetry.LlmCostUsd.Add((double)cost, new KeyValuePair<string, object?>("model", model));
        }

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
                AppTelemetry.CacheLookups.Add(1, new KeyValuePair<string, object?>("result", "miss"));
                return null;
            }

            // Another user (with wider access) may have produced this answer. Reusing it for someone who cannot
            // read its sources would leak their content, so check against *this* user's permissions now.
            var allowed = await retrieval.CanReadAllAsync(tenantId, userId, user.IsAdmin, candidate.SourceDocumentIds, ct);
            AppTelemetry.CacheLookups.Add(1, new KeyValuePair<string, object?>("result", allowed ? "hit" : "denied"));
            return allowed ? candidate : null;
        }
#pragma warning disable CA1031 // The cache is an optimisation: any failure means "miss", never "error".
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            AppTelemetry.CacheLookups.Add(1, new KeyValuePair<string, object?>("result", "error"));
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removed {Count} instruction-like sentence(s) from retrieved passages (conversation {ConversationId})")]
    private static partial void LogInjectionNeutralized(ILogger logger, int count, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Withheld an answer that repeated the system prompt (conversation {ConversationId})")]
    private static partial void LogLeakBlocked(ILogger logger, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record usage for a failed request")]
    private static partial void LogLedgerFailed(ILogger logger, Exception exception);
}
