using System.Text.Json;
using Domain;
using Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Ingestion;

/// <summary>
/// Background worker that drains the outbox. Each message is handled in its own DI scope with the message's
/// tenant restored. Transient failures are retried with exponential backoff; once attempts run out the message
/// is dead-lettered and the document is marked failed (so users see it and can reprocess).
/// </summary>
public sealed partial class OutboxWorker(
    IServiceScopeFactory scopes,
    OutboxStore store,
    IOptions<IngestionOptions> options,
    ILogger<OutboxWorker> logger) : BackgroundService
{
    private static readonly TimeSpan BusyDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.WorkerEnabled)
        {
            LogDisabled(logger);
            return;
        }

        var inFlight = new List<Task>();
        LogStarted(logger, o.Concurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                inFlight.RemoveAll(t => t.IsCompleted);
                var free = o.Concurrency - inFlight.Count;

                if (free <= 0)
                {
                    await Task.WhenAny(inFlight);
                    continue;
                }

                var batch = await store.ClaimAsync(free, TimeSpan.FromSeconds(o.LeaseSeconds), stoppingToken);
                if (batch.Count == 0)
                {
                    await Task.Delay(o.PollIntervalMs, stoppingToken);
                    continue;
                }

                inFlight.AddRange(batch.Select(m => Task.Run(() => HandleAsync(m, stoppingToken), CancellationToken.None)));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable CA1031 // The polling loop must survive any database hiccup.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogLoopError(logger, ex);
                await DelayQuietly(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        await Task.WhenAll(inFlight); // let in-flight work wind down (it observes the cancelled token)
    }

    private async Task HandleAsync(ClaimedMessage message, CancellationToken stoppingToken)
    {
        var o = options.Value;
        try
        {
            using var scope = scopes.CreateScope();
            // Restore the tenant so the DbContext's query filters see this message's data (and only it).
            scope.ServiceProvider.GetRequiredService<TenantContext>().TenantId = message.TenantId;

            var outcome = await Dispatch(scope.ServiceProvider, message, stoppingToken);
            if (outcome == ProcessOutcome.Busy)
            {
                await store.ReleaseAsync(message.Id, BusyDelay, CancellationToken.None);
            }
            else
            {
                await store.CompleteAsync(message.Id, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down mid-work: hand the message back without burning an attempt.
            await store.ReleaseAsync(message.Id, TimeSpan.Zero, CancellationToken.None);
        }
#pragma warning disable CA1031 // Any other failure is handled by the retry policy below.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await HandleFailure(message, ex, o);
        }
    }

    private static async Task<ProcessOutcome> Dispatch(IServiceProvider services, ClaimedMessage message, CancellationToken ct)
    {
        if (message.Type != OutboxMessage.DocumentUploaded)
        {
            throw new InvalidOperationException($"Unknown outbox message type '{message.Type}'.");
        }

        var payload = JsonSerializer.Deserialize<DocumentUploadedPayload>(message.Payload)
            ?? throw new InvalidOperationException("Outbox payload is empty.");
        return await services.GetRequiredService<DocumentIngestionProcessor>().ProcessAsync(payload.DocumentId, ct);
    }

    private async Task HandleFailure(ClaimedMessage message, Exception exception, IngestionOptions o)
    {
        var error = $"{exception.GetType().Name}: {exception.Message}";

        if (message.Attempts >= o.MaxAttempts)
        {
            LogDeadLettered(logger, exception, message.Id, message.Attempts);
            await store.DeadAsync(message.Id, error, CancellationToken.None);
            await MarkDocumentFailed(message, $"Processing failed after {message.Attempts} attempts: {exception.Message}");
            return;
        }

        var delay = BackoffFor(message.Attempts, o.RetryBaseSeconds);
        LogRetrying(logger, exception, message.Id, message.Attempts, o.MaxAttempts, delay.TotalSeconds);
        await store.RetryAsync(message.Id, delay, error, CancellationToken.None);
    }

    private async Task MarkDocumentFailed(ClaimedMessage message, string reason)
    {
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().TenantId = message.TenantId;
            var payload = JsonSerializer.Deserialize<DocumentUploadedPayload>(message.Payload);
            if (payload is not null)
            {
                await scope.ServiceProvider.GetRequiredService<DocumentIngestionProcessor>()
                    .FailAsync(message.TenantId, payload.DocumentId, reason, CancellationToken.None);
            }
        }
#pragma warning disable CA1031 // The dead-letter record is already written; this is a best-effort status update.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogMarkFailedError(logger, ex, message.Id);
        }
    }

    /// <summary>base · 2^(attempt-1), capped, plus up to 20% jitter so retries from many workers spread out.</summary>
    public static TimeSpan BackoffFor(int attempt, double baseSeconds)
    {
        if (baseSeconds <= 0)
        {
            return TimeSpan.Zero;
        }

        var seconds = Math.Min(baseSeconds * Math.Pow(2, Math.Max(0, attempt - 1)), MaxBackoff.TotalSeconds);
        var jitter = 1 + (Random.Shared.NextDouble() * 0.2);
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    private static async Task DelayQuietly(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion worker is disabled on this instance")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ingestion worker started (concurrency {Concurrency})")]
    private static partial void LogStarted(ILogger logger, int concurrency);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ingestion worker loop error")]
    private static partial void LogLoopError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox message {MessageId} failed (attempt {Attempt}/{Max}); retrying in {DelaySeconds:0.#}s")]
    private static partial void LogRetrying(ILogger logger, Exception exception, Guid messageId, int attempt, int max, double delaySeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox message {MessageId} dead-lettered after {Attempts} attempts")]
    private static partial void LogDeadLettered(ILogger logger, Exception exception, Guid messageId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not mark the document failed for outbox message {MessageId}")]
    private static partial void LogMarkFailedError(ILogger logger, Exception exception, Guid messageId);
}

/// <summary>Payload of <see cref="OutboxMessage.DocumentUploaded"/> messages.</summary>
public sealed record DocumentUploadedPayload(Guid DocumentId);
