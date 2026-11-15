// k6 load test for the Knowledge Assistant API.
//
//   docker compose -f docker-compose.prod.yml -f tests/load/docker-compose.loadtest.yml --env-file .env up -d
//   docker run --rm -i --network host -v "$PWD/tests/load:/s" grafana/k6 run /s/smoke.js
//
// Env: BASE_URL (default http://localhost:8088), KEYCLOAK_URL (default http://localhost:8081), VUS, DURATION.
// Needs the sample realm's password-grant client (kb-dev-cli); it exists only in the dev realm, never in production.
import http from "k6/http";
import { check, group, sleep } from "k6";
import { Rate, Trend } from "k6/metrics";

const BASE = __ENV.BASE_URL || "http://localhost:8088";
const KC = __ENV.KEYCLOAK_URL || "http://localhost:8081";

const chatLatency = new Trend("chat_ask_duration", true);
const unexpected = new Rate("unexpected_errors");

export const options = {
  scenarios: {
    readers: { executor: "constant-vus", vus: Number(__ENV.VUS || 20), duration: __ENV.DURATION || "30s", exec: "reader" },
    askers: { executor: "constant-vus", vus: Math.max(1, Math.floor(Number(__ENV.VUS || 20) / 4)), duration: __ENV.DURATION || "30s", exec: "asker" },
  },
  thresholds: {
    // Reads are plain indexed queries; chat includes embedding, hybrid retrieval, and a streamed answer.
    "http_req_duration{kind:read}": ["p(95)<300"],
    chat_ask_duration: ["p(95)<2000"],
    unexpected_errors: ["rate<0.01"],
    checks: ["rate>0.99"],
  },
};

function token() {
  const res = http.post(
    `${KC}/realms/kb/protocol/openid-connect/token`,
    { grant_type: "password", client_id: "kb-dev-cli", username: "alice", password: "alice-dev-pw" },
  );
  if (res.status !== 200) throw new Error(`login failed: ${res.status} ${res.body}`);
  return res.json("access_token");
}

export function setup() {
  const t = token();
  const auth = { headers: { Authorization: `Bearer ${t}` } };
  const json = { headers: { Authorization: `Bearer ${t}`, "Content-Type": "application/json", "Idempotency-Key": `k6-ws-${Date.now()}` } };
  const ws = http.post(`${BASE}/api/v1/workspaces`, JSON.stringify({ name: `k6-${Date.now()}`, description: "load test" }), json);
  if (ws.status !== 201) throw new Error(`workspace create failed: ${ws.status} ${ws.body}`);
  const workspaceId = ws.json("id");

  const text = "Remote work policy. Employees may work remotely up to three days per week with manager approval. " +
    "Expense policy. Meals while travelling are reimbursed up to fifty dollars per day with receipts.";
  const up = http.post(`${BASE}/api/v1/workspaces/${workspaceId}/documents`,
    { file: http.file(text, "policy.md", "text/markdown"), title: "Policy" }, auth);
  if (up.status !== 201 && up.status !== 202) throw new Error(`upload failed: ${up.status} ${up.body}`);

  // Wait for ingestion so chat has something to retrieve.
  for (let i = 0; i < 30; i++) {
    const d = http.get(`${BASE}/api/v1/workspaces/${workspaceId}/documents`, auth);
    if (d.status === 200 && d.body.toLowerCase().includes('"ready"')) break;
    sleep(1);
  }
  return { workspaceId };
}

function headers() {
  return { headers: { Authorization: `Bearer ${token()}` } };
}

let cached;
function auth() {
  // One token per VU: tokens last minutes, far longer than a run.
  if (!cached) cached = headers();
  return cached;
}

export function reader(data) {
  group("reads", () => {
    const a = auth();
    const tag = { tags: { kind: "read" } };
    const list = http.get(`${BASE}/api/v1/workspaces`, { ...a, ...tag });
    const docs = http.get(`${BASE}/api/v1/workspaces/${data.workspaceId}/documents`, { ...a, ...tag });
    const ok = check(list, { "workspaces 200": (r) => r.status === 200 }) && check(docs, { "documents 200": (r) => r.status === 200 });
    unexpected.add(!ok);
  });
  sleep(0.2);
}

export function asker(data) {
  const a = auth();
  const json = { headers: { ...a.headers, "Content-Type": "application/json" } };
  const conv = http.post(`${BASE}/api/v1/conversations`, JSON.stringify({ workspaceId: data.workspaceId, title: "k6" }), json);
  if (!check(conv, { "conversation 201": (r) => r.status === 201 })) { unexpected.add(true); return; }

  const q = ["How many days can I work remotely?", "What is the meal reimbursement limit?"][Math.floor(Math.random() * 2)];
  const res = http.post(`${BASE}/api/v1/conversations/${conv.json("id")}/messages`, JSON.stringify({ content: q }), json);
  chatLatency.add(res.timings.duration);
  const ok = check(res, { "ask 200": (r) => r.status === 200, "answer streamed": (r) => r.body.includes("event: done") || r.body.includes('"type":"done"') || r.body.length > 50 });
  unexpected.add(!ok);
  sleep(1);
}
