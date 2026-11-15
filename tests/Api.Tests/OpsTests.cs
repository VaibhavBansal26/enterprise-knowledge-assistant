using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Chat;
using Application.Telemetry;
using Application.Usage;
using Domain;
using Infrastructure.Chat;
using Infrastructure.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Api.Tests.ChatHelpers;

namespace Api.Tests;

/// <summary>
/// Safety behaviour inside the chat flow, the usage ledger, quotas, the dashboard data behind them, and telemetry.
/// Most tests run in a brand-new tenant so the dashboard numbers contain only what the test itself produced.
/// </summary>
[Collection(ApiFixtureGroup.Name)]
public sealed class OpsTests(ApiFactory factory)
{
    private static (TestUser Admin, TestUser Editor) NewTenant()
    {
        var tenant = Guid.NewGuid();
        return (new TestUser($"adm-{tenant:N}", tenant, "admin"), new TestUser($"edt-{tenant:N}", tenant, "editor"));
    }

    private static async Task<OpsSummaryResponse> SummaryAsync(HttpClient admin, int days = 7) =>
        (await admin.GetFromJsonAsync<OpsSummaryResponse>($"/api/v1/admin/ops/summary?days={days}"))!;

    private static async Task<(HttpClient Client, Guid WorkspaceId, Guid ConversationId)> SetUpAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host, TestUser admin, string title = "Benefits handbook", string text = LeaveText)
    {
        var client = ApiFactory.ClientAs(host, admin);
        var workspaceId = await CreateWorkspaceAsync(client);
        await UploadReadyAsync(client, workspaceId, title, text);
        return (client, workspaceId, await CreateConversationAsync(client, workspaceId));
    }

    private static async IAsyncEnumerable<LlmEvent> Say(string text, string stop = "end_turn")
    {
        foreach (var word in text.Split(' '))
        {
            yield return new LlmTextDelta(word + " ");
            await Task.Yield();
        }

        yield return new LlmCompleted("scripted-model", 120, 40, stop);
    }

    // ---- PII ----

    [Fact]
    public async Task Personal_data_in_questions_is_masked_before_it_is_stored_embedded_or_sent_to_the_model()
    {
        var llm = new RecordingLlm();
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(llm));
        var (client, _, conversationId) = await SetUpAsync(host, admin);

        var (_, events) = await AskAsync(client, conversationId,
            $"{LeaveQuestion} Reply to jane.doe@example.com, my card is 4111 1111 1111 1111 and my number is 415-555-2671.");

        var sent = Assert.Single(llm.Requests).Messages[^1].Content;
        Assert.DoesNotContain("jane.doe@example.com", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("4111", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("555-2671", sent, StringComparison.Ordinal);
        Assert.Contains("[EMAIL]", sent, StringComparison.Ordinal);
        Assert.Equal(3, Done(events).Data.GetProperty("piiRedactions").GetInt32());

        var detail = await client.GetFromJsonAsync<ConversationDetailResponse>($"/api/v1/conversations/{conversationId}");
        var stored = detail!.Messages[0].Content;
        Assert.DoesNotContain("jane.doe@example.com", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("4111", detail.Conversation.Title, StringComparison.Ordinal); // the title came from the masked question
        Assert.Contains("[CARD]", stored, StringComparison.Ordinal);
        Assert.Equal(3, (await SummaryAsync(client)).PiiRedactions);
    }

    [Fact]
    public async Task Masking_can_be_turned_off()
    {
        var llm = new RecordingLlm();
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(llm), ("Chat:RedactPii", "false"));
        var (client, _, conversationId) = await SetUpAsync(host, admin);

        await AskAsync(client, conversationId, $"{LeaveQuestion} contact jane.doe@example.com");

        Assert.Contains("jane.doe@example.com", Assert.Single(llm.Requests).Messages[^1].Content, StringComparison.Ordinal);
    }

    // ---- leak and grounding ----

    [Fact]
    public async Task An_answer_that_repeats_the_system_prompt_is_stopped_and_replaced()
    {
        var system = PromptTemplates.SystemPrompt(PromptTemplates.RagV1);
        var llm = new RecordingLlm { Behaviour = (_, _) => Say("Sure, here are my rules: " + string.Join(' ', system.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(20).Take(60))) };
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(llm), ("Chat:CacheEnabled", "false"));
        var (client, _, conversationId) = await SetUpAsync(host, admin);

        var (_, events) = await AskAsync(client, conversationId, LeaveQuestion);

        var done = Done(events).Data;
        Assert.Equal(ChatService.LeakBlockedAnswer, done.GetProperty("content").GetString());
        Assert.False(done.GetProperty("grounded").GetBoolean());
        Assert.Empty(done.GetProperty("citations").EnumerateArray());
        // The stream was cut early: far fewer words were sent than the model tried to produce.
        Assert.True(Text(events).Split(' ').Length < 40, "the leaked text should not have been streamed in full");
        Assert.Equal(1, (await SummaryAsync(client)).Outcomes.Refused);
    }

    [Fact]
    public async Task Answers_that_wander_off_their_sources_are_flagged_and_never_cached()
    {
        var llm = new RecordingLlm { Behaviour = (_, _) => Say("The company offers unlimited vacation days and free catered lunches every single day to all staff members [1].") };
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(llm));
        var (client, workspaceId, firstConversation) = await SetUpAsync(host, admin);

        var first = Done((await AskAsync(client, firstConversation, LeaveQuestion)).Events).Data;
        var second = Done((await AskAsync(client, await CreateConversationAsync(client, workspaceId), LeaveQuestion)).Events).Data;

        Assert.True(first.GetProperty("lowGrounding").GetBoolean());
        Assert.True(first.GetProperty("groundingScore").GetDouble() < 0.5);
        Assert.False(first.GetProperty("grounded").GetBoolean());
        Assert.False(second.GetProperty("fromCache").GetBoolean()); // a dubious answer must not be reused
        Assert.Equal(2, llm.Calls);
        var detail = await client.GetFromJsonAsync<ConversationDetailResponse>($"/api/v1/conversations/{firstConversation}");
        Assert.True(detail!.Messages[1].LowGrounding);
        Assert.Equal(2, (await SummaryAsync(client)).LowGroundingAnswers);
    }

    [Fact]
    public async Task Faithful_answers_score_high_and_are_not_flagged()
    {
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(new RecordingLlm()));
        var (client, _, conversationId) = await SetUpAsync(host, admin);

        var done = Done((await AskAsync(client, conversationId, LeaveQuestion)).Events).Data;

        Assert.False(done.GetProperty("lowGrounding").GetBoolean());
        Assert.True(done.GetProperty("groundingScore").GetDouble() >= 0.9);
        Assert.True(done.GetProperty("grounded").GetBoolean());
    }

    // ---- usage ledger and the dashboard ----

    [Fact]
    public async Task Every_outcome_is_counted_and_cost_follows_the_configured_prices()
    {
        var llm = new RecordingLlm();
        var (admin, _) = NewTenant();
        // $1 per token makes the expected cost exact: cost == tokens.
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(llm),
            ("Pricing:Models:extractive-fallback:InputPerMTok", "1000000"), ("Pricing:Models:extractive-fallback:OutputPerMTok", "1000000"),
            ("Usage:DefaultMonthlyCostLimitUsd", "0")); // at $1 per token the default $25 cap would (rightly) stop the test
        var (client, workspaceId, firstConversation) = await SetUpAsync(host, admin);

        var answered = Done((await AskAsync(client, firstConversation, LeaveQuestion)).Events).Data; // answered
        await AskAsync(client, await CreateConversationAsync(client, workspaceId), LeaveQuestion);       // cached
        await AskAsync(client, await CreateConversationAsync(client, workspaceId), "quantum chromodynamics gluon confinement"); // no sources
        llm.Behaviour = (_, _) => throw new LlmUnavailableException("down");                             // error
        await AskAsync(client, await CreateConversationAsync(client, workspaceId), "can parental leave be split into two blocks within the first year?");
        llm.Behaviour = null;

        var summary = await SummaryAsync(client);

        Assert.Equal(new OutcomeCounts(Questions: 4, Answered: 1, Cached: 1, NoSources: 1, Refused: 0, Error: 1, Cancelled: 0, QuotaExceeded: 0), summary.Outcomes);
        var chatTokens = answered.GetProperty("inputTokens").GetInt32() + answered.GetProperty("outputTokens").GetInt32();
        Assert.True(chatTokens > 0);
        Assert.Equal(chatTokens, summary.TotalCostUsd);
        Assert.Equal(chatTokens / 4m, summary.CostPerQuestionUsd);
        Assert.Equal(0.25, summary.CacheHitRate);
        Assert.Equal(0.25, summary.NoSourceRate);
        Assert.Equal(0.25, summary.ErrorRate);
        var chatModel = Assert.Single(summary.Models, m => m.Model == "extractive-fallback");
        Assert.Equal(chatTokens, chatModel.CostUsd);                  // all the spend is the chat model's
        Assert.Contains(summary.Models, m => m.Model == HashingEmbeddingClient.Model && m.CostUsd == 0); // embeddings are tracked separately, and free offline
        Assert.Contains(summary.TopUsers, u => u.UserId == admin.Sub && u.Questions == 4);
    }

    [Fact]
    public async Task Latency_percentiles_and_daily_series_are_reported()
    {
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(new RecordingLlm()), ("Chat:CacheEnabled", "false"));
        var (client, _, conversationId) = await SetUpAsync(host, admin);
        for (var i = 0; i < 5; i++)
        {
            await AskAsync(client, conversationId, $"{LeaveQuestion} number {i}");
        }

        var summary = await SummaryAsync(client);

        Assert.True(summary.Latency.P50Ms > 0);
        Assert.True(summary.Latency.P95Ms >= summary.Latency.P50Ms);
        Assert.True(summary.Latency.P99Ms >= summary.Latency.P95Ms);
        Assert.True(summary.FirstToken.P95Ms > 0);
        Assert.True(summary.FirstToken.P95Ms <= summary.Latency.P95Ms + 1);
        var today = Assert.Single(summary.Daily);
        Assert.Equal(5, today.Questions);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), today.Day);
        Assert.NotNull(summary.AverageGrounding);
    }

    [Fact]
    public async Task Pressing_stop_is_recorded_as_a_cancelled_question()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var llm = new RecordingLlm
        {
            Behaviour = (_, ct) => Hang(cancelled, ct),
        };
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(llm), ("Chat:CacheEnabled", "false"));
        var (client, _, conversationId) = await SetUpAsync(host, admin);

        using var stop = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/conversations/{conversationId}/messages") { Content = JsonContent.Create(new AskRequest(LeaveQuestion)) };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        await foreach (var e in Sse.StreamAsync(response, stop.Token))
        {
            if (e.Name == "delta")
            {
                await stop.CancelAsync();
                break;
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        OpsSummaryResponse summary;
        do
        {
            await Task.Delay(100);
            summary = await SummaryAsync(client);
        }
        while (summary.Outcomes.Cancelled == 0 && DateTime.UtcNow < deadline);

        Assert.Equal(1, summary.Outcomes.Cancelled);
    }

    private static async IAsyncEnumerable<LlmEvent> Hang(TaskCompletionSource cancelled, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        yield return new LlmTextDelta("Working on it ");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            cancelled.TrySetResult();
            throw;
        }
    }

    [Fact]
    public async Task The_dashboard_is_private_to_the_tenant_and_to_admins()
    {
        var (admin, editor) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(new RecordingLlm()));
        var (client, _, conversationId) = await SetUpAsync(host, admin);
        await AskAsync(client, conversationId, LeaveQuestion);

        var other = ApiFactory.ClientAs(host, TestAuth.AdminB);
        var otherSummary = await SummaryAsync(other);
        var asEditor = ApiFactory.ClientAs(host, editor);

        Assert.True((await SummaryAsync(client)).Outcomes.Questions >= 1);
        Assert.DoesNotContain(otherSummary.TopUsers, u => u.UserId == admin.Sub);
        Assert.Equal(HttpStatusCode.Forbidden, (await asEditor.GetAsync("/api/v1/admin/ops/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asEditor.GetAsync("/api/v1/admin/quota")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await asEditor.GetAsync("/api/v1/admin/evals/latest")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/admin/ops/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/admin/ops/summary?days=500")).StatusCode);
    }

    // ---- quotas ----

    [Fact]
    public async Task A_tenant_over_its_token_quota_is_refused_before_any_model_call_until_the_limit_is_raised()
    {
        var llm = new RecordingLlm();
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(llm), ("Chat:CacheEnabled", "false"));
        var (client, _, conversationId) = await SetUpAsync(host, admin);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/admin/quota", new SetQuotaRequest(1, null))).StatusCode);

        var first = await AskAsync(client, conversationId, LeaveQuestion);          // allowed: nothing used yet
        var second = await AskAsync(client, conversationId, $"{LeaveQuestion} again"); // now over the limit
        var callsBefore = llm.Calls;

        Assert.Equal(HttpStatusCode.OK, first.Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.Response.StatusCode);
        Assert.True(second.Response.Headers.Contains("Retry-After"));
        var problem = await second.Response.Content.ReadAsStringAsync();
        Assert.Contains("Monthly usage limit reached", problem, StringComparison.Ordinal);
        Assert.Equal(1, callsBefore); // the refused question never reached the model

        var quota = await client.GetFromJsonAsync<QuotaStatusResponse>("/api/v1/admin/quota");
        Assert.True(quota!.Exceeded);
        Assert.False(quota.IsDefault);
        Assert.Equal(1, quota.MonthlyTokenLimit);
        Assert.Equal(1, (await SummaryAsync(client)).Outcomes.QuotaExceeded);

        await client.PutAsJsonAsync("/api/v1/admin/quota", new SetQuotaRequest(null, null)); // unlimited
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(client, conversationId, $"{LeaveQuestion} once more")).Response.StatusCode);
    }

    [Fact]
    public async Task The_cost_quota_is_enforced_too_and_other_tenants_are_unaffected()
    {
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(new RecordingLlm()), ("Chat:CacheEnabled", "false"),
            ("Pricing:Models:extractive-fallback:InputPerMTok", "1000000"), ("Pricing:Models:extractive-fallback:OutputPerMTok", "1000000"));
        var (client, _, conversationId) = await SetUpAsync(host, admin);
        await client.PutAsJsonAsync("/api/v1/admin/quota", new SetQuotaRequest(null, 0.01m));

        var first = await AskAsync(client, conversationId, LeaveQuestion);
        var second = await AskAsync(client, conversationId, $"{LeaveQuestion} again");
        var neighbour = ApiFactory.ClientAs(host, TestAuth.AdminB);
        var neighbourConversation = await CreateConversationAsync(neighbour);

        Assert.Equal(HttpStatusCode.OK, first.Response.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AskAsync(neighbour, neighbourConversation, "hello there friend")).Response.StatusCode);
    }

    [Fact]
    public async Task Without_a_custom_quota_the_configured_defaults_apply_and_the_reset_date_is_next_month()
    {
        var (admin, _) = NewTenant();
        using var host = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Usage:DefaultMonthlyTokenLimit", "5000");
            b.UseSetting("Usage:DefaultMonthlyCostLimitUsd", "0");
        });
        var client = ApiFactory.ClientAs(host, admin);

        var quota = await client.GetFromJsonAsync<QuotaStatusResponse>("/api/v1/admin/quota");

        Assert.True(quota!.IsDefault);
        Assert.Equal(5000, quota.MonthlyTokenLimit);
        Assert.Null(quota.MonthlyCostLimitUsd); // 0 in configuration means unlimited
        Assert.False(quota.Exceeded);
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddMonths(1), quota.ResetsAt);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, 0)]
    [InlineData(null, -5)]
    public async Task Invalid_quotas_are_rejected(int? tokens, int? cost)
    {
        var response = await factory.AdminClient().PutAsJsonAsync("/api/v1/admin/quota", new SetQuotaRequest(tokens, cost));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- evaluation report endpoint ----

    [Fact]
    public async Task The_latest_eval_report_is_served_to_admins_and_absent_reports_are_a_clear_404()
    {
        var file = Path.Combine(Path.GetTempPath(), $"kb-eval-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            ranAt = DateTimeOffset.UtcNow,
            provider = "extractive-fallback + hashing-v1",
            cases = 12,
            passed = true,
            metrics = new[] { new { name = "retrieval_hit_rate", value = 0.95, baseline = 0.94, delta = 0.01, status = "ok" } },
            failures = Array.Empty<string>(),
        }));
        try
        {
            using var withReport = factory.WithWebHostBuilder(b => b.UseSetting("Evals:ResultsPath", file));
            using var withoutReport = factory.WithWebHostBuilder(b => b.UseSetting("Evals:ResultsPath", Path.Combine(Path.GetTempPath(), "no-such-report.json")));

            var report = await ApiFactory.ClientAs(withReport, TestAuth.AdminA).GetFromJsonAsync<EvalReportResponse>("/api/v1/admin/evals/latest");
            var missing = await ApiFactory.ClientAs(withoutReport, TestAuth.AdminA).GetAsync("/api/v1/admin/evals/latest");

            Assert.True(report!.Passed);
            Assert.Equal(12, report.Cases);
            Assert.Equal("retrieval_hit_rate", Assert.Single(report.Metrics).Name);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- telemetry ----

    [Fact]
    public async Task Chat_emits_metrics_by_outcome_and_spans_that_never_contain_the_question_or_the_answer()
    {
        var counted = new List<(string Name, long Value, string? Outcome)>();
        var histograms = new List<string>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AppTelemetry.Name)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (counted)
            {
                counted.Add((instrument.Name, value, tags.ToArray().FirstOrDefault(t => t.Key == "outcome").Value as string));
            }
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, _, _, _) =>
        {
            lock (histograms)
            {
                histograms.Add(instrument.Name);
            }
        });
        meterListener.Start();

        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == AppTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                lock (activities)
                {
                    activities.Add(a);
                }
            },
        };
        ActivitySource.AddActivityListener(activityListener);

        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(new RecordingLlm()));
        var (client, _, conversationId) = await SetUpAsync(host, admin);
        var question = "How many weeks of paid parental leave do employees receive?";

        var (_, events) = await AskAsync(client, conversationId, question);
        var answer = Done(events).Data.GetProperty("content").GetString()!;

        lock (counted)
        {
            Assert.Contains(counted, c => c is { Name: "kb.chat.requests", Outcome: "answered" });
            Assert.Contains(counted, c => c.Name == "kb.llm.tokens" && c.Value > 0);
        }

        lock (histograms)
        {
            Assert.Contains("kb.chat.duration", histograms);
            Assert.Contains("kb.chat.time_to_first_token", histograms);
            Assert.Contains("kb.retrieval.duration", histograms);
        }

        List<Activity> chat;
        lock (activities)
        {
            chat = activities.Where(a => a.OperationName.StartsWith("chat.", StringComparison.Ordinal)).ToList();
        }

        var root = Assert.Single(chat, a => a.OperationName == "chat.ask");
        Assert.Equal("answered", root.GetTagItem("kb.outcome"));
        foreach (var name in new[] { "chat.embed", "chat.retrieve", "chat.llm.stream" })
        {
            var child = Assert.Single(chat, a => a.OperationName == name);
            Assert.Equal(root.Id, child.ParentId); // one trace per question
        }

        // Privacy: nothing the user typed or the model wrote may appear in any span.
        var allTagText = string.Join('\n', chat.SelectMany(a => a.TagObjects).Select(t => $"{t.Key}={t.Value}"));
        Assert.DoesNotContain("parental", allTagText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(answer[..Math.Min(30, answer.Length)], allTagText, StringComparison.Ordinal);
        Assert.DoesNotContain(question, allTagText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ledger_rows_never_store_question_or_answer_text()
    {
        var (admin, _) = NewTenant();
        using var host = await TestHosts.StartWorkerHostAsync(factory, TestHosts.Llm(new RecordingLlm()));
        var (client, _, conversationId) = await SetUpAsync(host, admin);
        await AskAsync(client, conversationId, LeaveQuestion);

        await using var cmd = factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand(
            "SELECT row_to_json(l)::text FROM usage_ledger l WHERE conversation_id = @c");
        cmd.Parameters.AddWithValue("c", conversationId);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        Assert.Equal(2, rows.Count); // one chat row, one embedding row
        Assert.All(rows, r => Assert.DoesNotContain("parental", r, StringComparison.OrdinalIgnoreCase));
    }
}
