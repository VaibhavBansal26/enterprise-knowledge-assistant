using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using Application.Documents;
using Application.Ingestion;
using Application.Workspaces;
using Infrastructure.Ingestion;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pgvector;

namespace Api.Tests;

/// <summary>
/// The ingestion pipeline end to end: upload → outbox → worker → parse/chunk/embed → pgvector, plus retries,
/// dead-lettering, reprocessing and live status over SignalR. Each test runs its own host with the worker on.
/// </summary>
[Collection(ApiFixtureGroup.Name)]
public sealed class IngestionTests(ApiFactory factory)
{
    private NpgsqlDataSource Db => factory.Services.GetRequiredService<NpgsqlDataSource>();

    private const string PlantsText =
        "Photosynthesis is the process by which plants use sunlight, water and carbon dioxide to make glucose " +
        "and oxygen. Chlorophyll in the leaves absorbs the light that drives this reaction.";

    private const string FinanceText =
        "Quarterly revenue grew because the company expanded subscription pricing and reduced customer churn " +
        "in the enterprise segment, improving gross margin.";

    // ---- helpers ----

    /// <summary>Starts a host whose worker is on. Unfinished messages from other tests are cleared first.</summary>
    private async Task<WebApplicationFactory<Program>> StartHostAsync(
        Action<IServiceCollection>? services = null, params (string Key, string Value)[] settings)
    {
        await ExecuteAsync("DELETE FROM outbox_messages WHERE processed_at IS NULL AND dead_at IS NULL");
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

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Db.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Db.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<long> ChunkCountAsync(Guid documentId) =>
        ScalarAsync<long>("SELECT count(*) FROM document_chunks WHERE document_id = @d", ("d", documentId));

    private async Task<(int Attempts, bool Processed, bool Dead, string? LastError)> OutboxOfAsync(Guid documentId)
    {
        await using var cmd = Db.CreateCommand(
            """
            SELECT attempts, processed_at IS NOT NULL, dead_at IS NOT NULL, last_error
              FROM outbox_messages WHERE payload::jsonb ->> 'DocumentId' = @d ORDER BY created_at DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue("d", documentId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "no outbox message for the document");
        return (reader.GetInt32(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static async Task<Guid> CreateWorkspaceAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/workspaces", new CreateWorkspaceRequest($"ing-{Guid.NewGuid():N}", null));
        return (await response.Content.ReadFromJsonAsync<WorkspaceResponse>())!.Id;
    }

    private static string DocUrl(Guid workspaceId, Guid documentId) => $"/api/v1/workspaces/{workspaceId}/documents/{documentId}";

    /// <summary>An embedding client that fails its first <paramref name="failures"/> calls, as a flaky provider would.</summary>
    private sealed class FlakyEmbeddingClient(int failures) : IEmbeddingClient
    {
        private int _calls;
        private readonly HashingEmbeddingClient _inner = new();

        public string ModelId => _inner.ModelId;

        public int Calls => _calls;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) <= failures
                ? throw new HttpRequestException("simulated embedding provider outage")
                : _inner.EmbedAsync(texts, cancellationToken);
    }

    private static Action<IServiceCollection> UseEmbedding(IEmbeddingClient client) => services =>
    {
        services.RemoveAll<IEmbeddingClient>();
        services.AddSingleton(client);
    };

    // ---- happy paths ----

    [Fact]
    public async Task Markdown_upload_becomes_ready_with_chunks_and_vectors()
    {
        using var host = await StartHostAsync();
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);

        var created = await TestFiles.UploadOkAsync(client, workspaceId, "plants.md", TestFiles.Markdown($"# Plants\n\n{PlantsText}"));
        var ready = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Ready");

        Assert.True(ready.ChunkCount >= 1);
        Assert.Equal(ready.ChunkCount, await ChunkCountAsync(created.Id));
        Assert.Equal(DocumentChunkDimensions, await ScalarAsync<int>(
            "SELECT vector_dims(embedding) FROM document_chunks WHERE document_id = @d LIMIT 1", ("d", created.Id)));
        var outbox = await OutboxOfAsync(created.Id);
        Assert.True(outbox.Processed);
        Assert.Equal(1, outbox.Attempts);
    }

    private const int DocumentChunkDimensions = Domain.DocumentChunk.EmbeddingDimensions;

    [Fact]
    public async Task PDF_and_DOCX_uploads_are_parsed_chunked_and_embedded()
    {
        using var host = await StartHostAsync();
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);

        var pdf = await TestFiles.UploadOkAsync(client, workspaceId, "report.pdf", TestFiles.Pdf("Quarterly findings about zebrafish genomics.", "Second line of the report."));
        var docx = await TestFiles.UploadOkAsync(client, workspaceId, "memo.docx", TestFiles.Docx("Memo about the wombat migration plan.", "Approved by the committee."));

        await TestFiles.WaitForStatusAsync(client, workspaceId, pdf.Id, "Ready");
        await TestFiles.WaitForStatusAsync(client, workspaceId, docx.Id, "Ready");

        Assert.Contains("zebrafish", await ScalarAsync<string>(
            "SELECT string_agg(content, ' ') FROM document_chunks WHERE document_id = @d", ("d", pdf.Id)), StringComparison.Ordinal);
        Assert.Contains("wombat", await ScalarAsync<string>(
            "SELECT string_agg(content, ' ') FROM document_chunks WHERE document_id = @d", ("d", docx.Id)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Long_documents_are_split_into_multiple_bounded_overlapping_chunks()
    {
        using var host = await StartHostAsync(null, ("Ingestion:ChunkSizeChars", "400"), ("Ingestion:ChunkOverlapChars", "60"));
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);
        var paragraphs = Enumerable.Range(1, 12).Select(i => $"Paragraph {i} discusses topic{i} in some detail so that the document is long enough to need several chunks to cover it.");

        var created = await TestFiles.UploadOkAsync(client, workspaceId, "long.md", TestFiles.Markdown(string.Join("\n\n", paragraphs)));
        var ready = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Ready");

        Assert.True(ready.ChunkCount > 2);
        Assert.True(await ScalarAsync<int>("SELECT max(length(content)) FROM document_chunks WHERE document_id = @d", ("d", created.Id)) <= 400);
        Assert.Equal(ready.ChunkCount, await ScalarAsync<long>(
            "SELECT count(DISTINCT index) FROM document_chunks WHERE document_id = @d", ("d", created.Id)));
    }

    [Fact]
    public async Task Vector_search_over_the_hnsw_index_finds_the_relevant_document()
    {
        using var host = await StartHostAsync();
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);
        var plants = await TestFiles.UploadOkAsync(client, workspaceId, "plants.md", TestFiles.Markdown(PlantsText));
        var finance = await TestFiles.UploadOkAsync(client, workspaceId, "finance.md", TestFiles.Markdown(FinanceText));
        await TestFiles.WaitForStatusAsync(client, workspaceId, plants.Id, "Ready");
        await TestFiles.WaitForStatusAsync(client, workspaceId, finance.Id, "Ready");

        async Task<Guid> NearestAsync(string question)
        {
            await using var cmd = Db.CreateCommand(
                "SELECT document_id FROM document_chunks WHERE workspace_id = @w ORDER BY embedding <=> @q LIMIT 1");
            cmd.Parameters.AddWithValue("w", workspaceId);
            cmd.Parameters.AddWithValue("q", new Vector(HashingEmbeddingClient.Embed(question)));
            return (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        Assert.Equal(plants.Id, await NearestAsync("how do plants turn sunlight into glucose with chlorophyll"));
        Assert.Equal(finance.Id, await NearestAsync("did subscription pricing reduce churn and improve revenue"));
    }

    [Fact]
    public async Task Chunks_belong_to_the_documents_tenant()
    {
        using var host = await StartHostAsync();
        var tenantB = ApiFactory.ClientAs(host, TestAuth.AdminB);
        var workspaceId = await CreateWorkspaceAsync(tenantB);

        var created = await TestFiles.UploadOkAsync(tenantB, workspaceId);
        await TestFiles.WaitForStatusAsync(tenantB, workspaceId, created.Id, "Ready");

        Assert.Equal(TestAuth.TenantB, await ScalarAsync<Guid>(
            "SELECT DISTINCT tenant_id FROM document_chunks WHERE document_id = @d", ("d", created.Id)));
    }

    // ---- upload validation ----

    [Fact]
    public async Task Unsupported_types_are_rejected_with_415()
    {
        var client = factory.AdminClient();
        var workspaceId = await CreateWorkspaceAsync(client);

        var response = await TestFiles.UploadAsync(client, workspaceId, "malware.exe", [1, 2, 3, 4]);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Content_must_match_the_claimed_type()
    {
        var client = factory.AdminClient();
        var workspaceId = await CreateWorkspaceAsync(client);

        var fakePdf = await TestFiles.UploadAsync(client, workspaceId, "fake.pdf", TestFiles.Markdown("just text, not a PDF"));
        var fakeDocx = await TestFiles.UploadAsync(client, workspaceId, "fake.docx", TestFiles.Markdown("just text, not a DOCX"));
        var binaryAsText = await TestFiles.UploadAsync(client, workspaceId, "binary.txt", [0x00, 0x01, 0x02, 0xFF, 0xFE]);

        Assert.Equal(HttpStatusCode.BadRequest, fakePdf.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, fakeDocx.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, binaryAsText.StatusCode);
    }

    [Fact]
    public async Task Empty_files_are_rejected_with_400()
    {
        var client = factory.AdminClient();
        var workspaceId = await CreateWorkspaceAsync(client);

        var response = await TestFiles.UploadAsync(client, workspaceId, "empty.md", []);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Files_over_the_limit_are_rejected_with_413_and_nothing_is_stored()
    {
        using var host = factory.WithWebHostBuilder(b => b.UseSetting("Storage:MaxUploadBytes", "2048"));
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);
        var before = Directory.GetFiles(factory.BlobRoot, "*", SearchOption.AllDirectories).Length;

        var response = await TestFiles.UploadAsync(client, workspaceId, "big.md", TestFiles.Markdown(new string('x', 5000)));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(before, Directory.GetFiles(factory.BlobRoot, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public async Task Path_traversal_in_the_file_name_cannot_escape_storage()
    {
        var client = factory.AdminClient();
        var workspaceId = await CreateWorkspaceAsync(client);

        var response = await TestFiles.UploadAsync(client, workspaceId, "../../../../etc/evil.md", TestFiles.Markdown("harmless"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DocumentResponse>())!;
        Assert.Equal("evil.md", created.FileName); // directory parts dropped
        Assert.All(Directory.GetFiles(factory.BlobRoot, "*", SearchOption.AllDirectories),
            f => Assert.StartsWith(factory.BlobRoot, f, StringComparison.Ordinal));
    }

    // ---- authorization ----

    [Fact]
    public async Task Only_writers_with_access_can_upload()
    {
        var admin = factory.AdminClient();
        var workspaceId = await CreateWorkspaceAsync(admin);
        await admin.PutAsJsonAsync($"/api/v1/workspaces/{workspaceId}/members/{TestAuth.ViewerA.Sub}", new UpsertMemberRequest("Editor"));

        var viewer = await TestFiles.UploadAsync(factory.CreateClientAs(TestAuth.ViewerA), workspaceId);
        var outsider = await TestFiles.UploadAsync(factory.CreateClientAs(TestAuth.EditorA2), workspaceId);
        var otherTenant = await TestFiles.UploadAsync(factory.CreateClientAs(TestAuth.AdminB), workspaceId);
        var anonymous = await TestFiles.UploadAsync(factory.CreateClient(), workspaceId);

        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, outsider.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---- idempotent uploads ----

    [Fact]
    public async Task Retrying_an_upload_with_the_same_key_creates_one_document()
    {
        var client = factory.AdminClient();
        var workspaceId = await CreateWorkspaceAsync(client);
        var key = Guid.NewGuid().ToString();
        var bytes = TestFiles.Markdown("# Same file\n\nbody");

        var first = await TestFiles.UploadAsync(client, workspaceId, "same.md", bytes, idempotencyKey: key);
        var retry = await TestFiles.UploadAsync(client, workspaceId, "same.md", bytes, idempotencyKey: key); // new multipart boundary
        var different = await TestFiles.UploadAsync(client, workspaceId, "same.md", TestFiles.Markdown("other content"), idempotencyKey: key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", Assert.Single(retry.Headers.GetValues("Idempotent-Replayed")));
        Assert.Equal(first.Headers.Location, retry.Headers.Location);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal(1, await ScalarAsync<long>("SELECT count(*) FROM documents WHERE workspace_id = @w", ("w", workspaceId)));
    }

    // ---- failures, retries, dead-letter ----

    [Fact]
    public async Task Corrupt_files_fail_immediately_without_retrying()
    {
        using var host = await StartHostAsync();
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);

        var created = await TestFiles.UploadOkAsync(client, workspaceId, "broken.pdf", TestFiles.Markdown("%PDF-1.4\nthis is not really a pdf"));
        var failed = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Failed");

        Assert.Contains("PDF", failed.FailureReason, StringComparison.Ordinal);
        var outbox = await OutboxOfAsync(created.Id);
        Assert.Equal(1, outbox.Attempts); // permanent: no point retrying
        Assert.True(outbox.Processed);
        Assert.False(outbox.Dead);
        Assert.Equal(0, await ChunkCountAsync(created.Id));
    }

    [Fact]
    public async Task Documents_without_text_fail_with_a_clear_reason()
    {
        using var host = await StartHostAsync();
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);

        var created = await TestFiles.UploadOkAsync(client, workspaceId, "blank.md", TestFiles.Markdown("   \n\n  \n"));
        var failed = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Failed");

        Assert.Contains("no extractable text", failed.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transient_failures_are_retried_until_they_succeed()
    {
        var flaky = new FlakyEmbeddingClient(failures: 2);
        using var host = await StartHostAsync(UseEmbedding(flaky), ("Ingestion:MaxAttempts", "5"));
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);

        var created = await TestFiles.UploadOkAsync(client, workspaceId);
        var ready = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Ready");

        Assert.True(ready.ChunkCount > 0);
        var outbox = await OutboxOfAsync(created.Id);
        Assert.Equal(3, outbox.Attempts); // two failures, then success
        Assert.True(outbox.Processed);
        Assert.Equal(3, flaky.Calls);
    }

    [Fact]
    public async Task Exhausted_retries_dead_letter_the_message_and_fail_the_document_until_reprocessed()
    {
        // 3 failures with MaxAttempts=3 exhaust the first message; the 4th embedding call succeeds.
        var flaky = new FlakyEmbeddingClient(failures: 3);
        using var host = await StartHostAsync(UseEmbedding(flaky), ("Ingestion:MaxAttempts", "3"));
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);

        var created = await TestFiles.UploadOkAsync(client, workspaceId);
        var failed = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Failed");

        Assert.Contains("after 3 attempts", failed.FailureReason, StringComparison.Ordinal);
        var dead = await OutboxOfAsync(created.Id);
        Assert.True(dead.Dead);
        Assert.False(dead.Processed);
        Assert.Equal(3, dead.Attempts);
        Assert.Contains("simulated embedding provider outage", dead.LastError, StringComparison.Ordinal);

        // Recovery: an operator (or the user) reprocesses the failed document, now that the provider is healthy.
        var reprocess = await client.PostAsync($"{DocUrl(workspaceId, created.Id)}/reprocess", null);
        Assert.Equal(HttpStatusCode.Accepted, reprocess.StatusCode);
        var ready = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Ready");
        Assert.True(ready.ChunkCount > 0);
        Assert.Null(ready.FailureReason);
    }

    [Fact]
    public async Task Reprocessing_replaces_chunks_instead_of_duplicating_them()
    {
        using var host = await StartHostAsync();
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);
        var created = await TestFiles.UploadOkAsync(client, workspaceId, "plants.md", TestFiles.Markdown(PlantsText));
        var first = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Ready");

        var reprocess = await client.PostAsync($"{DocUrl(workspaceId, created.Id)}/reprocess", null);
        var second = await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Ready");
        // Wait for the second run to actually finish (the status flips Pending -> Processing -> Ready).
        await Task.Delay(300);

        Assert.Equal(HttpStatusCode.Accepted, reprocess.StatusCode);
        Assert.Equal(first.ChunkCount, second.ChunkCount);
        Assert.Equal(first.ChunkCount, await ChunkCountAsync(created.Id));
    }

    [Fact]
    public async Task Reprocessing_a_document_that_is_already_queued_returns_409()
    {
        var client = factory.AdminClient(); // no worker: the document stays Pending
        var workspaceId = await CreateWorkspaceAsync(client);
        var created = await TestFiles.UploadOkAsync(client, workspaceId);

        var response = await client.PostAsync($"{DocUrl(workspaceId, created.Id)}/reprocess", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_a_document_removes_its_chunks_and_stored_file()
    {
        using var host = await StartHostAsync();
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);
        var created = await TestFiles.UploadOkAsync(client, workspaceId);
        await TestFiles.WaitForStatusAsync(client, workspaceId, created.Id, "Ready");
        var storageKey = await ScalarAsync<string>("SELECT storage_key FROM documents WHERE id = @d", ("d", created.Id));
        Assert.True(await ChunkCountAsync(created.Id) > 0);
        Assert.True(File.Exists(Path.Combine(factory.BlobRoot, storageKey)));

        var response = await client.DeleteAsync(DocUrl(workspaceId, created.Id));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(0, await ChunkCountAsync(created.Id));
        Assert.False(File.Exists(Path.Combine(factory.BlobRoot, storageKey)));
    }

    [Fact]
    public async Task A_document_deleted_before_processing_is_skipped_cleanly()
    {
        var client = factory.AdminClient(); // upload while no worker is running
        var workspaceId = await CreateWorkspaceAsync(client);
        var created = await TestFiles.UploadOkAsync(client, workspaceId);
        await client.DeleteAsync(DocUrl(workspaceId, created.Id));

        // Now start a worker without clearing the queue: it must complete the message, not fail or resurrect it.
        using var host = factory.WithWebHostBuilder(b => b.UseSetting("Ingestion:WorkerEnabled", "true"));
        _ = host.CreateClient();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        (int Attempts, bool Processed, bool Dead, string? LastError) outbox;
        do
        {
            await Task.Delay(50);
            outbox = await OutboxOfAsync(created.Id);
        }
        while (!outbox.Processed && DateTime.UtcNow < deadline);

        Assert.True(outbox.Processed);
        Assert.Equal(0, await ChunkCountAsync(created.Id));
    }

    // ---- queue and lock primitives ----

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_message()
    {
        await ExecuteAsync("DELETE FROM outbox_messages WHERE processed_at IS NULL AND dead_at IS NULL");
        var tenant = Guid.NewGuid();
        var ids = Enumerable.Range(0, 24).Select(_ => Guid.NewGuid()).ToList();
        foreach (var id in ids)
        {
            await ExecuteAsync(
                "INSERT INTO outbox_messages (id, created_at, tenant_id, type, payload, attempts, next_attempt_at) VALUES (@id, now(), @t, 'test.claim', '{}', 0, now())",
                ("id", id), ("t", tenant));
        }

        var store = factory.Services.GetRequiredService<OutboxStore>();
        var claimed = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var batch = await store.ClaimAsync(3, TimeSpan.FromSeconds(60), CancellationToken.None);
                if (batch.Count == 0)
                {
                    return;
                }

                foreach (var m in batch)
                {
                    claimed.Add(m.Id);
                    await store.CompleteAsync(m.Id, CancellationToken.None);
                }
            }
        })));

        Assert.Equal(24, claimed.Count);
        Assert.Equal(24, claimed.Distinct().Count()); // SKIP LOCKED: no message handed out twice
        Assert.All(ids, id => Assert.Contains(id, claimed));
    }

    [Fact]
    public async Task A_crashed_workers_claim_expires_and_the_message_is_picked_up_again()
    {
        await ExecuteAsync("DELETE FROM outbox_messages WHERE processed_at IS NULL AND dead_at IS NULL");
        var id = Guid.NewGuid();
        await ExecuteAsync(
            "INSERT INTO outbox_messages (id, created_at, tenant_id, type, payload, attempts, next_attempt_at) VALUES (@id, now(), @t, 'test.lease', '{}', 0, now())",
            ("id", id), ("t", Guid.NewGuid()));
        var store = factory.Services.GetRequiredService<OutboxStore>();

        var first = await store.ClaimAsync(1, TimeSpan.FromSeconds(1), CancellationToken.None); // worker "crashes" now
        var whileLeased = await store.ClaimAsync(1, TimeSpan.FromSeconds(1), CancellationToken.None);
        await Task.Delay(1300);
        var afterExpiry = await store.ClaimAsync(1, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(id, Assert.Single(first).Id);
        Assert.Empty(whileLeased);
        var again = Assert.Single(afterExpiry);
        Assert.Equal(id, again.Id);
        Assert.Equal(2, again.Attempts);
        await store.CompleteAsync(id, CancellationToken.None);
    }

    [Fact]
    public async Task Distributed_lock_excludes_others_until_released_or_expired()
    {
        var locks = factory.Services.GetRequiredService<IDistributedLock>();
        var key = $"test:{Guid.NewGuid():N}";

        var first = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        var contender = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        await first!.DisposeAsync();
        var afterRelease = await locks.TryAcquireAsync(key, TimeSpan.FromMilliseconds(250), CancellationToken.None);
        var whileHeld = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(500); // the short lease expires on its own
        var afterExpiry = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(contender);
        Assert.NotNull(afterRelease);
        Assert.Null(whileHeld);
        Assert.NotNull(afterExpiry);
        await afterExpiry!.DisposeAsync();
    }

    [Fact]
    public async Task A_stale_lock_handle_cannot_release_someone_elses_lock()
    {
        var locks = factory.Services.GetRequiredService<IDistributedLock>();
        var key = $"test:{Guid.NewGuid():N}";
        var stale = await locks.TryAcquireAsync(key, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await Task.Delay(400); // expires
        var current = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);

        await stale!.DisposeAsync(); // must not delete the new owner's lock
        var intruder = await locks.TryAcquireAsync(key, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.NotNull(current);
        Assert.Null(intruder);
        await current!.DisposeAsync();
    }

    // ---- live status over SignalR ----

    private static HubConnection Connect(WebApplicationFactory<Program> host, TestUser user)
    {
        _ = host.CreateClient(); // ensures the test server is started
        return new HubConnectionBuilder()
            .WithUrl(new Uri(host.Server.BaseAddress, "hubs/ingestion"), o =>
            {
                o.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling; // TestServer has no WebSocket server
                o.AccessTokenProvider = () => Task.FromResult<string?>(TestAuth.Token(user));
            })
            .Build();
    }

    [Fact]
    public async Task Subscribers_receive_live_status_updates_until_the_document_is_ready()
    {
        using var host = await StartHostAsync();
        var client = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(client);
        var events = Channel.CreateUnbounded<DocumentStatusChanged>();
        await using var hub = Connect(host, TestAuth.AdminA);
        hub.On<DocumentStatusChanged>("documentStatus", e => events.Writer.TryWrite(e));
        await hub.StartAsync();
        await hub.InvokeAsync("Subscribe", workspaceId);

        var created = await TestFiles.UploadOkAsync(client, workspaceId);

        var seen = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var e in events.Reader.ReadAllAsync(timeout.Token))
        {
            Assert.Equal(created.Id, e.DocumentId);
            Assert.Equal(workspaceId, e.WorkspaceId);
            seen.Add(e.Status);
            if (e.Status == "Ready")
            {
                Assert.True(e.ChunkCount > 0);
                break;
            }
        }

        Assert.Equal(["Processing", "Ready"], seen);
    }

    [Fact]
    public async Task Hub_subscription_follows_the_same_access_rules_as_the_api()
    {
        using var host = await StartHostAsync();
        var admin = ApiFactory.ClientAs(host, TestAuth.AdminA);
        var workspaceId = await CreateWorkspaceAsync(admin);

        await using var outsider = Connect(host, TestAuth.EditorA2); // same tenant, not a member
        await using var otherTenant = Connect(host, TestAuth.AdminB);
        await using var member = Connect(host, TestAuth.EditorA1);
        await outsider.StartAsync();
        await otherTenant.StartAsync();
        await member.StartAsync();
        await admin.PutAsJsonAsync($"/api/v1/workspaces/{workspaceId}/members/{TestAuth.EditorA1.Sub}", new UpsertMemberRequest("Viewer"));

        await Assert.ThrowsAsync<HubException>(() => outsider.InvokeAsync("Subscribe", workspaceId));
        await Assert.ThrowsAsync<HubException>(() => otherTenant.InvokeAsync("Subscribe", workspaceId));
        await member.InvokeAsync("Subscribe", workspaceId); // a read-only member may watch
    }

    [Fact]
    public async Task The_hub_rejects_unauthenticated_connections()
    {
        using var host = await StartHostAsync();
        _ = host.CreateClient();
        await using var anonymous = new HubConnectionBuilder()
            .WithUrl(new Uri(host.Server.BaseAddress, "hubs/ingestion"), o =>
            {
                o.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }
}
