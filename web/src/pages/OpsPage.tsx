import { useState } from "react";
import { Link } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, type OpsSummary, type QuotaStatus } from "../api/client";
import { ErrorMessage } from "../components";

const usd = (n: number) => (n < 0.01 && n > 0 ? `$${n.toFixed(4)}` : `$${n.toFixed(2)}`);
const pct = (n: number) => `${(n * 100).toFixed(1)}%`;
const ms = (n: number) => (n >= 1000 ? `${(n / 1000).toFixed(2)} s` : `${Math.round(n)} ms`);
const num = (n: number) => n.toLocaleString();

function Kpi({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div style={{ border: "1px solid #e1e4e8", borderRadius: 8, padding: "0.6rem 0.8rem", minWidth: 140 }} title={hint}>
      <div style={{ fontSize: "0.75rem", color: "#666" }}>{label}</div>
      <div style={{ fontSize: "1.35rem", fontWeight: 600 }}>{value}</div>
    </div>
  );
}

const OUTCOME_COLORS: Record<string, string> = {
  answered: "#2e7d32",
  cached: "#66bb6a",
  noSources: "#9e9e9e",
  refused: "#f9a825",
  error: "#c62828",
  cancelled: "#8d6e63",
  quotaExceeded: "#6a1b9a",
};

function OutcomeBar({ o }: { o: OpsSummary["outcomes"] }) {
  const parts = (Object.keys(OUTCOME_COLORS) as (keyof typeof OUTCOME_COLORS)[]).map((k) => ({
    key: k,
    count: (o as unknown as Record<string, number>)[k],
  }));
  const total = Math.max(o.questions, 1);
  return (
    <div>
      <div style={{ display: "flex", height: 18, borderRadius: 4, overflow: "hidden", background: "#eee" }} role="img" aria-label="Outcome mix">
        {parts.map((p) => (
          <div key={p.key} title={`${p.key}: ${p.count}`} style={{ width: `${(p.count / total) * 100}%`, background: OUTCOME_COLORS[p.key] }} />
        ))}
      </div>
      <div style={{ display: "flex", gap: "1rem", flexWrap: "wrap", fontSize: "0.8rem", marginTop: 6 }}>
        {parts.map((p) => (
          <span key={p.key}>
            <span style={{ display: "inline-block", width: 10, height: 10, background: OUTCOME_COLORS[p.key], marginRight: 4 }} />
            {p.key} {p.count}
          </span>
        ))}
      </div>
    </div>
  );
}

function DailyChart({ daily }: { daily: OpsSummary["daily"] }) {
  if (daily.length === 0) return <p style={{ color: "#666" }}>No activity in this period.</p>;
  const max = Math.max(...daily.map((d) => d.questions), 1);
  return (
    <div style={{ display: "flex", gap: 6, alignItems: "flex-end", height: 110 }} role="img" aria-label="Questions per day">
      {daily.map((d) => (
        <div key={d.day} style={{ flex: 1, maxWidth: 48, textAlign: "center" }} title={`${d.day}: ${d.questions} questions, ${usd(d.costUsd)}, p95 ${ms(d.p95LatencyMs)}`}>
          <div style={{ height: Math.max(3, (d.questions / max) * 80), background: "#4a78c2", borderRadius: "3px 3px 0 0" }} />
          <div style={{ fontSize: "0.65rem", color: "#666" }}>{d.day.slice(5)}</div>
        </div>
      ))}
    </div>
  );
}

function UsageBar({ used, limit, label }: { used: number; limit: number | null; label: string }) {
  if (limit === null) return <div>{label}: {num(used)} used (no limit)</div>;
  const ratio = Math.min(used / limit, 1);
  return (
    <div>
      <div style={{ fontSize: "0.85rem" }}>
        {label}: {num(used)} / {num(limit)} ({pct(used / limit)})
      </div>
      <div style={{ height: 8, background: "#eee", borderRadius: 4 }}>
        <div style={{ width: `${ratio * 100}%`, height: 8, borderRadius: 4, background: ratio >= 1 ? "#c62828" : ratio > 0.8 ? "#f9a825" : "#2e7d32" }} />
      </div>
    </div>
  );
}

function QuotaCard({ quota }: { quota: QuotaStatus }) {
  const qc = useQueryClient();
  const [tokens, setTokens] = useState(quota.monthlyTokenLimit?.toString() ?? "");
  const [cost, setCost] = useState(quota.monthlyCostLimitUsd?.toString() ?? "");
  const save = useMutation({
    mutationFn: () => api.setQuota(tokens.trim() ? Number(tokens) : null, cost.trim() ? Number(cost) : null),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["ops"] }),
  });

  return (
    <div style={{ border: "1px solid #e1e4e8", borderRadius: 8, padding: "0.8rem" }}>
      <strong>Monthly quota</strong>{" "}
      <small style={{ color: "#666" }}>
        resets {new Date(quota.resetsAt).toLocaleDateString()} {quota.isDefault && "· default limits"}
      </small>
      {quota.exceeded && <div role="alert" style={{ color: "#c62828", margin: "4px 0" }}>Limit reached: new questions are being refused until the reset or a higher limit.</div>}
      <div style={{ display: "grid", gap: 8, margin: "0.5rem 0" }}>
        <UsageBar used={quota.tokensUsed} limit={quota.monthlyTokenLimit} label="Tokens" />
        <UsageBar used={Math.round(quota.costUsedUsd * 100) / 100} limit={quota.monthlyCostLimitUsd} label="Estimated cost (USD)" />
      </div>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          save.mutate();
        }}
        style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}
      >
        <input aria-label="Monthly token limit" placeholder="Token limit (blank = none)" value={tokens} onChange={(e) => setTokens(e.target.value)} inputMode="numeric" />
        <input aria-label="Monthly cost limit" placeholder="Cost limit USD (blank = none)" value={cost} onChange={(e) => setCost(e.target.value)} inputMode="decimal" />
        <button type="submit" disabled={save.isPending}>
          Save limits
        </button>
      </form>
      <ErrorMessage error={save.error} />
    </div>
  );
}

function EvalCard() {
  const evals = useQuery({ queryKey: ["ops", "evals"], queryFn: () => api.getLatestEvals(), retry: false });
  const missing = evals.error instanceof ApiError && evals.error.status === 404;
  return (
    <div style={{ border: "1px solid #e1e4e8", borderRadius: 8, padding: "0.8rem" }}>
      <strong>Evaluation scores</strong>
      {evals.isLoading && <p>Loading…</p>}
      {missing && <p style={{ color: "#666" }}>No evaluation report yet. Run <code>dotnet test --filter Category=Eval</code> or publish a report to the configured path.</p>}
      {!missing && <ErrorMessage error={evals.error} />}
      {evals.data && (
        <>
          <div style={{ margin: "4px 0", fontSize: "0.85rem" }}>
            <span style={{ color: evals.data.passed ? "#2e7d32" : "#c62828", fontWeight: 700 }}>{evals.data.passed ? "PASS" : "FAIL"}</span> · {evals.data.cases} cases ·{" "}
            <code>{evals.data.provider}</code> · {new Date(evals.data.ranAt).toLocaleString()}
          </div>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: "0.85rem" }}>
            <thead>
              <tr style={{ textAlign: "left", borderBottom: "1px solid #ccc" }}>
                <th>Metric</th>
                <th>Score</th>
                <th>Baseline</th>
                <th>Δ</th>
              </tr>
            </thead>
            <tbody>
              {evals.data.metrics.map((m) => (
                <tr key={m.name} style={{ borderBottom: "1px solid #eee", color: m.status === "ok" ? undefined : "#c62828" }}>
                  <td>{m.name}</td>
                  <td>{m.value}</td>
                  <td>{m.baseline ?? "—"}</td>
                  <td>{m.delta ?? "—"}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {evals.data.failures.map((f) => (
            <div key={f} role="alert" style={{ color: "#c62828", fontSize: "0.85rem" }}>
              {f}
            </div>
          ))}
        </>
      )}
    </div>
  );
}

export function OpsPage() {
  const [days, setDays] = useState(7);
  const summary = useQuery({ queryKey: ["ops", "summary", days], queryFn: () => api.getOpsSummary(days), refetchInterval: 30_000 });
  const s = summary.data;

  return (
    <section>
      <Link to="/">← Workspaces</Link>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
        <h2>Operations</h2>
        <label>
          Period{" "}
          <select value={days} onChange={(e) => setDays(Number(e.target.value))}>
            <option value={1}>Last 24 hours</option>
            <option value={7}>Last 7 days</option>
            <option value={30}>Last 30 days</option>
          </select>
        </label>
      </div>
      <ErrorMessage error={summary.error} />
      {summary.isLoading && <p>Loading…</p>}

      {s && (
        <div style={{ display: "grid", gap: "1.25rem" }}>
          <div style={{ display: "flex", gap: 12, flexWrap: "wrap" }}>
            <Kpi label="Questions" value={num(s.outcomes.questions)} />
            <Kpi label="Latency p50" value={ms(s.latency.p50Ms)} />
            <Kpi label="Latency p95" value={ms(s.latency.p95Ms)} hint="95% of answers finished faster than this" />
            <Kpi label="First token p95" value={ms(s.firstToken.p95Ms)} />
            <Kpi label="Spend" value={usd(s.totalCostUsd)} hint="Estimated from configured per-model prices" />
            <Kpi label="Cost / question" value={usd(s.costPerQuestionUsd)} />
            <Kpi label="Cache hit rate" value={pct(s.cacheHitRate)} />
            <Kpi label="No-source rate" value={pct(s.noSourceRate)} />
            <Kpi label="Error rate" value={pct(s.errorRate)} />
            <Kpi label="Avg grounding" value={s.averageGrounding === null ? "—" : s.averageGrounding.toFixed(2)} hint="Share of answer statements supported by their sources" />
            <Kpi label="Low-grounding answers" value={num(s.lowGroundingAnswers)} />
            <Kpi label="Injection removed" value={num(s.injectionNeutralized)} hint="Instruction-like sentences stripped from documents" />
            <Kpi label="PII masked" value={num(s.piiRedactions)} />
          </div>

          <div>
            <h3>Outcomes</h3>
            <OutcomeBar o={s.outcomes} />
          </div>

          <div>
            <h3>Questions per day</h3>
            <DailyChart daily={s.daily} />
          </div>

          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(300px, 1fr))", gap: "1rem" }}>
            <QuotaCard key={`${s.quota.monthlyTokenLimit}-${s.quota.monthlyCostLimitUsd}`} quota={s.quota} />
            <EvalCard />
          </div>

          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(300px, 1fr))", gap: "1rem" }}>
            <div>
              <h3>By model</h3>
              <table style={{ width: "100%", fontSize: "0.85rem", borderCollapse: "collapse" }}>
                <thead>
                  <tr style={{ textAlign: "left", borderBottom: "1px solid #ccc" }}>
                    <th>Model</th>
                    <th>Calls</th>
                    <th>Tokens in/out</th>
                    <th>Cost</th>
                  </tr>
                </thead>
                <tbody>
                  {s.models.map((m) => (
                    <tr key={m.model} style={{ borderBottom: "1px solid #eee" }}>
                      <td>{m.model}</td>
                      <td>{num(m.requests)}</td>
                      <td>
                        {num(m.inputTokens)} / {num(m.outputTokens)}
                      </td>
                      <td>{usd(m.costUsd)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div>
              <h3>Top users by spend</h3>
              <table style={{ width: "100%", fontSize: "0.85rem", borderCollapse: "collapse" }}>
                <thead>
                  <tr style={{ textAlign: "left", borderBottom: "1px solid #ccc" }}>
                    <th>User</th>
                    <th>Questions</th>
                    <th>Cost</th>
                  </tr>
                </thead>
                <tbody>
                  {s.topUsers.map((u) => (
                    <tr key={u.userId} style={{ borderBottom: "1px solid #eee" }}>
                      <td>
                        <code>{u.userId.slice(0, 8)}</code>
                      </td>
                      <td>{num(u.questions)}</td>
                      <td>{usd(u.costUsd)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}
    </section>
  );
}
