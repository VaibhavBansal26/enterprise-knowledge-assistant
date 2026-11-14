using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Api.Auth;
using Api.Web;
using Application.Abstractions;
using Application.Chat;
using Application.Common;
using Domain;
using Infrastructure.Chat;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Api.Endpoints;

/// <summary>Private chat conversations and the streaming question endpoint.</summary>
public static partial class ChatEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/conversations").WithTags("Chat").RequireAuthorization(Policies.Read);

        group.MapPost("/", Create).AddEndpointFilter<ValidationFilter<CreateConversationRequest>>();
        group.MapGet("/", List).AddEndpointFilter<ValidationFilter<ListQuery>>();
        group.MapGet("/{id:guid}", Get);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/messages", Ask)
            .AddEndpointFilter<ValidationFilter<AskRequest>>()
            .WithMetadata(new NoIdempotencyAttribute()); // a streamed response cannot be buffered for replay
        group.MapPut("/{id:guid}/messages/{messageId:guid}/feedback", SetFeedback)
            .AddEndpointFilter<ValidationFilter<FeedbackRequest>>();

        return app;
    }

    private static IResult ConversationNotFound() => Results.Problem(statusCode: 404, title: "Conversation not found");

    private static async Task<Conversation?> LoadOwnedAsync(Guid id, AppDbContext db, ICurrentUser me, bool track, CancellationToken ct)
    {
        IQueryable<Conversation> query = track ? db.Conversations : db.Conversations.AsNoTracking();
        return await query.FirstOrDefaultAsync(c => c.Id == id && c.UserId == me.UserId, ct);
    }

    private static async Task<IResult> Create(
        CreateConversationRequest request,
        AppDbContext db,
        ITenantContext tenant,
        ICurrentUser me,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request.WorkspaceId is { } workspaceId)
        {
            var (_, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: false, track: false, db, authorization, user, ct);
            if (error is not null)
            {
                return error;
            }
        }

        var conversation = new Conversation(tenant.TenantId!.Value, me.UserId!, request.WorkspaceId, request.Title?.Trim() ?? string.Empty);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/v1/conversations/{conversation.Id}", ToResponse(conversation));
    }

    private static async Task<IResult> List([AsParameters] ListQuery list, AppDbContext db, ICurrentUser me, CancellationToken ct)
    {
        var query = db.Conversations.AsNoTracking().Where(c => c.UserId == me.UserId).OrderByDescending(c => c.UpdatedAt).ThenBy(c => c.Id);
        return Results.Ok(await query.ToPagedAsync(list, ToResponse, ct));
    }

    private static async Task<IResult> Get(Guid id, AppDbContext db, ICurrentUser me, CancellationToken ct)
    {
        var conversation = await LoadOwnedAsync(id, db, me, track: false, ct);
        if (conversation is null)
        {
            return ConversationNotFound();
        }

        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

        return Results.Ok(new ConversationDetailResponse(ToResponse(conversation), messages.Select(ToResponse).ToList()));
    }

    private static async Task<IResult> Delete(Guid id, AppDbContext db, ICurrentUser me, CancellationToken ct)
    {
        var conversation = await LoadOwnedAsync(id, db, me, track: true, ct);
        if (conversation is null)
        {
            return ConversationNotFound();
        }

        db.Conversations.Remove(conversation); // messages go with it (cascade)
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetFeedback(
        Guid id, Guid messageId, FeedbackRequest request, AppDbContext db, ICurrentUser me, CancellationToken ct)
    {
        var conversation = await LoadOwnedAsync(id, db, me, track: false, ct);
        var message = conversation is null
            ? null
            : await db.ChatMessages.FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id && m.Role == ChatRole.Assistant, ct);
        if (message is null)
        {
            return Results.Problem(statusCode: 404, title: "Message not found");
        }

        message.SetFeedback(request.Rating == "none" ? null : request.Rating);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Streams the answer as Server-Sent Events: <c>sources</c> (passages considered), <c>delta</c> (text pieces),
    /// then <c>done</c> (final text, citations, usage) or <c>error</c>. Anything that can be refused (not found, not
    /// allowed, rate limited) is decided before streaming starts, so it returns an ordinary HTTP error.
    /// </summary>
    private static async Task<IResult> Ask(
        Guid id,
        AskRequest request,
        HttpContext http,
        AppDbContext db,
        ICurrentUser me,
        IAuthorizationService authorization,
        ClaimsPrincipal user,
        ChatService chat,
        IRateLimiter limiter,
        IOptions<ChatOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var conversation = await LoadOwnedAsync(id, db, me, track: true, ct);
        if (conversation is null)
        {
            return ConversationNotFound();
        }

        // A scoped conversation is only usable while the user can still read that workspace.
        if (conversation.WorkspaceId is { } workspaceId)
        {
            var (_, error) = await WorkspaceAccess.LoadAsync(workspaceId, write: false, track: false, db, authorization, user, ct);
            if (error is not null)
            {
                return error;
            }
        }

        var limited = await CheckChatRateLimitAsync(limiter, me.UserId!, options.Value.RequestsPerMinute, http, ct);
        if (limited is not null)
        {
            return limited;
        }

        var logger = loggerFactory.CreateLogger(typeof(ChatEndpoints));
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers["X-Accel-Buffering"] = "no"; // tell reverse proxies not to buffer the stream
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.StartAsync(ct);

        try
        {
            await foreach (var chatEvent in chat.AskAsync(conversation, request.Content, ct))
            {
                switch (chatEvent)
                {
                    case SourcesEvent sources:
                        await WriteEvent(http.Response, "sources", new { sources = sources.Sources }, ct);
                        break;
                    case DeltaEvent delta:
                        await WriteEvent(http.Response, "delta", new { text = delta.Text }, ct);
                        break;
                    case DoneEvent done:
                        await WriteEvent(http.Response, "done", done, ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user pressed Stop or left; cancellation already reached the model call. Nothing to send.
        }
        catch (LlmUnavailableException ex)
        {
            LogLlmUnavailable(logger, ex);
            await WriteEvent(http.Response, "error", new { code = "llm_unavailable", message = ex.Message }, CancellationToken.None);
        }
#pragma warning disable CA1031 // After streaming has started the only way to report a failure is an error event.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogChatFailed(logger, ex, id);
            await WriteEvent(http.Response, "error", new { code = "internal", message = "Something went wrong while answering. Please try again." }, CancellationToken.None);
        }

        return Results.Empty;
    }

    private static async Task<IResult?> CheckChatRateLimitAsync(
        IRateLimiter limiter, string userId, int perMinute, HttpContext http, CancellationToken ct)
    {
        try
        {
            var result = await limiter.TryAcquireAsync($"chat:{userId}", ct, perMinute, 60);
            if (result.Allowed)
            {
                return null;
            }

            http.Response.Headers.RetryAfter = result.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many questions",
                detail: $"Chat is limited to {perMinute} questions per minute. Retry after {result.RetryAfterSeconds} seconds.");
        }
#pragma warning disable CA1031 // Like the global limiter, this fails open: a Redis outage must not disable chat.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogLimiterDown(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ChatEndpoints)), ex);
            return null;
        }
    }

    private static async Task WriteEvent(HttpResponse response, string name, object data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }

    private static ConversationResponse ToResponse(Conversation c) => new(c.Id, c.WorkspaceId, c.Title, c.CreatedAt, c.UpdatedAt);

    private static MessageResponse ToResponse(ChatMessage m) => new(
        m.Id,
        m.Role == ChatRole.User ? "user" : "assistant",
        m.Content,
        m.CitationsJson is null ? [] : JsonSerializer.Deserialize<List<CitationDto>>(m.CitationsJson, Json) ?? [],
        m.Model,
        m.PromptVersion,
        m.FromCache,
        m.Feedback,
        m.CreatedAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat could not reach the language model")]
    private static partial void LogLlmUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Chat failed for conversation {ConversationId}")]
    private static partial void LogChatFailed(ILogger logger, Exception exception, Guid conversationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat rate limiter unavailable; allowing request")]
    private static partial void LogLimiterDown(ILogger logger, Exception exception);
}
