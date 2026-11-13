using Npgsql;

namespace Infrastructure.Ingestion;

public sealed record ClaimedMessage(Guid Id, Guid TenantId, string Type, string Payload, int Attempts);

/// <summary>
/// Queue operations over the <c>outbox_messages</c> table. Claiming uses <c>FOR UPDATE SKIP LOCKED</c>, so any
/// number of workers can poll concurrently without blocking each other or processing the same message twice
/// at once; a claim is a time-limited lease, so a crashed worker's messages become claimable again.
/// Plain SQL on purpose: these are system-level operations that must not go through tenant query filters.
/// </summary>
public sealed class OutboxStore(NpgsqlDataSource dataSource)
{
    private const string ClaimSql = """
        UPDATE outbox_messages
           SET attempts = attempts + 1,
               locked_until = now() + make_interval(secs => @lease)
         WHERE id IN (
               SELECT id FROM outbox_messages
                WHERE processed_at IS NULL
                  AND dead_at IS NULL
                  AND next_attempt_at <= now()
                  AND (locked_until IS NULL OR locked_until < now())
                ORDER BY next_attempt_at, created_at
                LIMIT @batch
                  FOR UPDATE SKIP LOCKED)
        RETURNING id, tenant_id, type, payload, attempts
        """;

    public async Task<IReadOnlyList<ClaimedMessage>> ClaimAsync(int batch, TimeSpan lease, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(ClaimSql);
        cmd.Parameters.AddWithValue("lease", lease.TotalSeconds);
        cmd.Parameters.AddWithValue("batch", batch);

        var messages = new List<ClaimedMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            messages.Add(new ClaimedMessage(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4)));
        }

        return messages;
    }

    public Task CompleteAsync(Guid id, CancellationToken ct) =>
        ExecuteAsync(
            "UPDATE outbox_messages SET processed_at = now(), locked_until = NULL, last_error = NULL WHERE id = @id",
            id, ct);

    /// <summary>Schedule another attempt after <paramref name="delay"/>.</summary>
    public Task RetryAsync(Guid id, TimeSpan delay, string error, CancellationToken ct) =>
        ExecuteAsync(
            """
            UPDATE outbox_messages
               SET next_attempt_at = now() + make_interval(secs => @delay), locked_until = NULL, last_error = @error
             WHERE id = @id
            """,
            id, ct, ("delay", delay.TotalSeconds), ("error", Truncate(error)));

    /// <summary>Retries exhausted: park the message as dead. It is kept for inspection and never claimed again.</summary>
    public Task DeadAsync(Guid id, string error, CancellationToken ct) =>
        ExecuteAsync(
            "UPDATE outbox_messages SET dead_at = now(), locked_until = NULL, last_error = @error WHERE id = @id",
            id, ct, ("error", Truncate(error)));

    /// <summary>
    /// Give a claim back without counting it as a failed attempt (graceful shutdown, or the work is busy elsewhere).
    /// </summary>
    public Task ReleaseAsync(Guid id, TimeSpan delay, CancellationToken ct) =>
        ExecuteAsync(
            """
            UPDATE outbox_messages
               SET attempts = GREATEST(attempts - 1, 0),
                   next_attempt_at = now() + make_interval(secs => @delay),
                   locked_until = NULL
             WHERE id = @id
            """,
            id, ct, ("delay", delay.TotalSeconds));

    private async Task ExecuteAsync(string sql, Guid id, CancellationToken ct, params (string Name, object Value)[] extra)
    {
        await using var cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("id", id);
        foreach (var (name, value) in extra)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Truncate(string text) => text.Length <= 2000 ? text : text[..2000];
}
