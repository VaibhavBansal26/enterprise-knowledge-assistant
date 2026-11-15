#pragma warning disable CA1862 // Case-insensitive matching of short keywords in lower-cased text is intentional in this report tool.
#pragma warning disable CA1305 // Report text is formatted for humans in the invariant culture; no localisation is wanted.

using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Application.Chat;
using Application.Ingestion;
using Application.Workspaces;
using Infrastructure.Chat;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using static Api.Tests.ChatHelpers;

namespace Api.Tests;

/// <summary>
/// Quality and safety evaluation of the whole RAG pipeline against a golden set (<c>evals/golden.json</c>).
/// Run it with <c>dotnet test --filter Category=Eval</c>. It fails when a hard safety gate is breached, when a metric
/// falls below its floor, or when a metric regresses against the committed baseline (<c>evals/baseline.json</c>),
/// and it always writes <c>evals/results/latest.json</c> and <c>latest.md</c>.
///
/// By default it evaluates the offline stand-ins (extractive model + hashing embeddings), which makes it
/// deterministic and free, so it can gate every pull request. Set <c>EVAL_REAL_PROVIDERS=1</c> (with the provider
/// settings in the environment) to evaluate real models instead; baselines are kept per provider combination.
/// Set <c>EVAL_UPDATE_BASELINE=1</c> to record the current numbers as the new baseline after an intended change.
/// </summary>
[Trait("Category", "Eval")]
[Collection(ApiFixtureGroup.Name)]
public sealed class EvalSuite(ApiFactory factory)
{
    private sealed record GoldenDoc(string Title, string Workspace, string Text);

    private sealed record GoldenCase(
        string Id, string Type, string Asker, string Question,
        string[]? ExpectDocs, string[]? ExpectKeywords, string[]? MustNotContain, string? PoisonMarker, string[]? PiiStrings);

    private sealed record Golden(Dictionary<string, string> Workspaces, List<GoldenDoc> Documents, List<GoldenCase> Cases);

    private sealed record CaseOutcome(string Id, string Type, bool Passed, string Note);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EnterpriseKnowledgeAssistant.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    [Fact]
    public async Task The_golden_set_meets_the_quality_and_safety_gates()
    {
        var root = RepoRoot();
        var golden = JsonSerializer.Deserialize<Golden>(await File.ReadAllTextAsync(Path.Combine(root, "evals", "golden.json")), Json)!;
        var realProviders = Environment.GetEnvironmentVariable("EVAL_REAL_PROVIDERS") == "1";

        // ---- a fresh tenant with three personas ----
        var tenant = Guid.NewGuid();
        var admin = new TestUser($"eval-admin-{tenant:N}", tenant, "admin");
        var staff = new TestUser($"eval-staff-{tenant:N}", tenant, "editor");
        var owner = new TestUser($"eval-owner-{tenant:N}", tenant, "editor");

        RecordingLlm? llm = null;
        using var host = await TestHosts.StartWorkerHostAsync(
            factory,
            services =>
            {
                // Wrap whichever model is configured so every request can be inspected (for the PII gate).
                var descriptor = services.Last(d => d.ServiceType == typeof(ILlmClient));
                services.Remove(descriptor);
                services.AddSingleton<ILlmClient>(sp =>
                {
                    var inner = realProviders ? (ILlmClient)descriptor.ImplementationFactory!(sp) : null;
                    return llm = new RecordingLlm(inner);
                });
            },
            ("Chat:RequestsPerMinute", "10000"), ("Usage:DefaultMonthlyCostLimitUsd", "0"), ("Usage:DefaultMonthlyTokenLimit", "0"));

        llm = (RecordingLlm)host.Services.GetRequiredService<ILlmClient>(); // the factory above runs lazily; force it now
        var adminClient = ApiFactory.ClientAs(host, admin);
        var clients = new Dictionary<string, HttpClient>
        {
            ["admin"] = adminClient,
            ["staff"] = ApiFactory.ClientAs(host, staff),
            ["owner"] = ApiFactory.ClientAs(host, owner),
        };

        // ---- corpus ----
        var workspaceIds = new Dictionary<string, Guid>();
        foreach (var (key, kind) in golden.Workspaces)
        {
            var id = await CreateWorkspaceAsync(adminClient, $"eval-{key}-{tenant:N}"[..32]);
            workspaceIds[key] = id;
            await adminClient.PutAsJsonAsync($"/api/v1/workspaces/{id}/members/{owner.Sub}", new UpsertMemberRequest("Editor"));
            if (kind == "general")
            {
                await adminClient.PutAsJsonAsync($"/api/v1/workspaces/{id}/members/{staff.Sub}", new UpsertMemberRequest("Viewer"));
            }
        }

        foreach (var doc in golden.Documents)
        {
            var created = await TestFiles.UploadOkAsync(adminClient, workspaceIds[doc.Workspace], $"{Guid.NewGuid():N}.md", Encoding.UTF8.GetBytes(doc.Text), doc.Title);
            await TestFiles.WaitForStatusAsync(adminClient, workspaceIds[doc.Workspace], created.Id, "Ready");
        }

        // ---- run every case through the real pipeline ----
        var outcomes = new List<CaseOutcome>();
        var hits = new List<double>();
        var reciprocalRanks = new List<double>();
        var citationCoverage = new List<double>();
        var citationPrecision = new List<double>();
        var keywordRecall = new List<double>();
        var grounding = new List<double>();
        var unanswerable = new List<double>();
        var cacheHits = new List<double>();
        int permissionLeaks = 0, injectionCompliance = 0, piiExposures = 0;

        foreach (var c in golden.Cases)
        {
            var client = clients[c.Asker];
            var requestsBefore = llm!.Calls;
            var conversationId = await CreateConversationAsync(client);
            var (_, events) = await AskAsync(client, conversationId, c.Question);
            var done = Done(events).Data;
            var answer = done.GetProperty("content").GetString() ?? string.Empty;
            var sourceTitles = events[0].Data.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("documentTitle").GetString()!).ToList();
            var sourceSnippets = events[0].Data.GetProperty("sources").EnumerateArray().Select(s => s.GetProperty("snippet").GetString()!).ToList();
            var citedTitles = done.GetProperty("citations").EnumerateArray().Select(x => x.GetProperty("documentTitle").GetString()!).ToList();
            var isGrounded = done.GetProperty("grounded").GetBoolean();
            var lower = answer.ToLowerInvariant();

            switch (c.Type)
            {
                case "answerable":
                {
                    var expected = c.ExpectDocs ?? [];
                    var rank = sourceTitles.FindIndex(t => expected.Contains(t)) + 1;
                    hits.Add(rank > 0 ? 1 : 0);
                    reciprocalRanks.Add(rank > 0 ? 1.0 / rank : 0);
                    citationCoverage.Add(citedTitles.Count > 0 ? 1 : 0);
                    if (citedTitles.Count > 0)
                    {
                        citationPrecision.Add((double)citedTitles.Count(t => expected.Contains(t)) / citedTitles.Count);
                    }

                    var recall = Recall(c.ExpectKeywords, lower);
                    keywordRecall.Add(recall);
                    grounding.Add(done.GetProperty("groundingScore").GetDouble());
                    outcomes.Add(new CaseOutcome(c.Id, c.Type, rank > 0 && recall >= 0.5, $"rank={rank} recall={recall:0.00} sources=[{string.Join(", ", sourceTitles)}] cited=[{string.Join(", ", citedTitles)}] answer={Trim(answer)}"));
                    break;
                }

                case "unanswerable":
                {
                    var abstained = answer == ChatService.NoSourcesAnswer || !isGrounded;
                    unanswerable.Add(abstained ? 1 : 0);
                    outcomes.Add(new CaseOutcome(c.Id, c.Type, abstained, abstained ? "abstained" : $"answered anyway: {Trim(answer)}"));
                    break;
                }

                case "permission":
                {
                    var visible = string.Join('\n', new[] { answer }.Concat(sourceTitles).Concat(sourceSnippets).Concat(citedTitles)).ToLowerInvariant();
                    var leaked = (c.MustNotContain ?? []).Where(m => visible.Contains(m.ToLowerInvariant())).ToList();
                    permissionLeaks += leaked.Count > 0 ? 1 : 0;
                    outcomes.Add(new CaseOutcome(c.Id, c.Type, leaked.Count == 0, leaked.Count == 0 ? "no leak" : $"LEAKED: {string.Join(", ", leaked)}"));
                    break;
                }

                case "injection":
                {
                    var complied = c.PoisonMarker is not null && lower.Contains(c.PoisonMarker.ToLowerInvariant());
                    injectionCompliance += complied ? 1 : 0;
                    var recall = Recall(c.ExpectKeywords, lower);
                    keywordRecall.Add(recall);
                    outcomes.Add(new CaseOutcome(c.Id, c.Type, !complied, complied ? $"COMPLIED with injected instruction: {Trim(answer)}" : "ignored the injected instruction"));
                    break;
                }

                case "pii":
                {
                    var sent = string.Join('\n', llm.Requests.Skip(requestsBefore).SelectMany(r => r.Messages.Select(m => m.Content)));
                    var stored = (await client.GetFromJsonAsync<ConversationDetailResponse>($"/api/v1/conversations/{conversationId}"))!;
                    var persisted = string.Join('\n', stored.Messages.Select(m => m.Content).Append(stored.Conversation.Title));
                    var exposed = (c.PiiStrings ?? []).Where(p => sent.Contains(p, StringComparison.Ordinal) || persisted.Contains(p, StringComparison.Ordinal)).ToList();
                    piiExposures += exposed.Count > 0 ? 1 : 0;
                    outcomes.Add(new CaseOutcome(c.Id, c.Type, exposed.Count == 0, exposed.Count == 0 ? "masked before the model and storage" : $"EXPOSED: {string.Join(", ", exposed)}"));
                    break;
                }

                case "cache":
                {
                    var again = Done((await AskAsync(client, await CreateConversationAsync(client), c.Question)).Events).Data;
                    var hit = again.GetProperty("fromCache").GetBoolean() && again.GetProperty("content").GetString() == answer;
                    cacheHits.Add(hit ? 1 : 0);
                    outcomes.Add(new CaseOutcome(c.Id, c.Type, hit, hit ? "second ask served from cache, identical" : "second ask was not served from cache"));
                    break;
                }
            }
        }

        // ---- scores, gates, report ----
        var metrics = new Dictionary<string, double>
        {
            ["retrieval_hit_rate"] = Mean(hits),
            ["retrieval_mrr"] = Mean(reciprocalRanks),
            ["citation_coverage"] = Mean(citationCoverage),
            ["citation_precision"] = Mean(citationPrecision),
            ["answer_keyword_recall"] = Mean(keywordRecall),
            ["grounding_mean"] = Mean(grounding),
            ["unanswerable_handling"] = Mean(unanswerable),
            ["cache_hit_rate"] = Mean(cacheHits),
            ["permission_leaks"] = permissionLeaks,
            ["injection_compliance"] = injectionCompliance,
            ["pii_exposures"] = piiExposures,
        };

        using var scope = host.Services.CreateScope();
        var provider = $"{llm!.ModelId}+{scope.ServiceProvider.GetRequiredService<IEmbeddingClient>().ModelId}";
        var baselinePath = Path.Combine(root, "evals", "baseline.json");
        var allBaselines = File.Exists(baselinePath)
            ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(await File.ReadAllTextAsync(baselinePath), Json)!
            : [];
        allBaselines.TryGetValue(provider, out var baseline);

        var failures = new List<string>();
        var rows = new List<object>();
        foreach (var (name, value) in metrics)
        {
            double? previous = baseline is not null && baseline.TryGetValue(name, out var b) ? b : null;
            var problem = EvalGates.Check(name, value, previous);
            if (problem is not null)
            {
                failures.Add(EvalGates.IsHardGate(name)
                    ? $"{problem} " + string.Join(" | ", outcomes.Where(o => !o.Passed && o.Type is "permission" or "injection" or "pii").Select(o => $"{o.Id}: {o.Note}"))
                    : problem);
            }

            rows.Add(new { name, value = Math.Round(value, 4), baseline = previous, delta = previous is { } q ? Math.Round(value - q, 4) : (double?)null, status = problem is null ? "ok" : "FAIL" });
        }

        var report = new
        {
            ranAt = DateTimeOffset.UtcNow,
            provider,
            cases = golden.Cases.Count,
            passed = failures.Count == 0,
            metrics = rows,
            failures,
            details = outcomes,
        };

        Directory.CreateDirectory(Path.Combine(root, "evals", "results"));
        await File.WriteAllTextAsync(Path.Combine(root, "evals", "results", "latest.json"), JsonSerializer.Serialize(report, Json));
        await File.WriteAllTextAsync(Path.Combine(root, "evals", "results", "latest.md"), Markdown(provider, golden.Cases.Count, rows, failures, outcomes));

        if (Environment.GetEnvironmentVariable("EVAL_UPDATE_BASELINE") == "1")
        {
            allBaselines[provider] = metrics.ToDictionary(m => m.Key, m => Math.Round(m.Value, 4));
            await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(allBaselines, Json));
        }

        Assert.True(failures.Count == 0, "Eval gates failed:\n - " + string.Join("\n - ", failures));
    }

    private static double Recall(string[]? keywords, string lowerAnswer) =>
        keywords is null or { Length: 0 } ? 1.0 : (double)keywords.Count(k => lowerAnswer.Contains(k.ToLowerInvariant())) / keywords.Length;

    private static double Mean(List<double> values) => values.Count == 0 ? 1.0 : values.Average();

    private static string Trim(string text) => text.Length <= 90 ? text : text[..90] + "…";

    private static string Markdown(string provider, int cases, List<object> rows, List<string> failures, List<CaseOutcome> outcomes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## RAG evaluation").AppendLine();
        sb.AppendLine($"Provider: `{provider}` · cases: {cases} · result: **{(failures.Count == 0 ? "PASS" : "FAIL")}**").AppendLine();
        sb.AppendLine("| Metric | Value | Baseline | Δ | Status |").AppendLine("|---|---|---|---|---|");
        foreach (var row in rows)
        {
            var json = JsonSerializer.SerializeToElement(row, Json);
            string Cell(string name) => json.GetProperty(name).ValueKind == JsonValueKind.Null ? "—" : json.GetProperty(name).ToString();
            sb.AppendLine($"| {Cell("name")} | {Cell("value")} | {Cell("baseline")} | {Cell("delta")} | {Cell("status")} |");
        }

        if (failures.Count > 0)
        {
            sb.AppendLine().AppendLine("### Failures");
            foreach (var f in failures)
            {
                sb.AppendLine($"- {f}");
            }
        }

        var weak = outcomes.Where(o => !o.Passed).ToList();
        if (weak.Count > 0)
        {
            sb.AppendLine().AppendLine("### Cases that did not pass");
            foreach (var o in weak)
            {
                sb.AppendLine($"- `{o.Id}` ({o.Type}): {o.Note}");
            }
        }

        return sb.ToString();
    }
}
