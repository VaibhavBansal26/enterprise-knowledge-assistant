import path from "node:path";
import { BASE, DASH, ROOT, caption, click, expect, hover, keycloakLogin, sleep, type } from "./lib.mjs";
import { slides } from "./slides.mjs";

const asset = (f) => path.join(ROOT, "assets", f);
const HR = "Northwind HR & Policies";

const app = (page) => page.getByRole("heading", { name: "Knowledge Assistant" });
const row = (page, text) => page.getByRole("listitem").filter({ hasText: text });
const answered = async (page, before) => {
  // an answer is complete once its feedback buttons appear
  await expect(page.getByTitle("Helpful", { exact: true })).toHaveCount(before + 1, { timeout: 45000 });
  await sleep(900);
};
async function ask(page, question, before) {
  await type(page, page.getByPlaceholder("Ask a question about your documents…"), question);
  await click(page, page.getByRole("button", { name: "Ask", exact: true }), { pause: 200 });
  await answered(page, before);
}
/** Opens an app page; if the redirect sign-in hangs on "Signing in…" (it occasionally does on a busy laptop), reload. */
async function go(page, url) {
  for (let i = 0; i < 3; i++) {
    await page.goto(url);
    try {
      await expect(app(page)).toBeVisible({ timeout: 20000 });
      return;
    } catch {
      /* reload and try again */
    }
  }
  await expect(app(page)).toBeVisible();
}
async function slide(scene, id) {
  await scene.page.setContent(slides[id]);
  scene.ready();
}
async function openWorkspace(page) {
  await click(page, page.getByRole("link", { name: HR }));
  await expect(page.getByRole("heading", { name: HR })).toBeVisible();
}
async function upload(page, file) {
  await click(page, page.locator('input[type="file"]'), { pause: 100 }).catch(() => {});
  await page.locator('input[type="file"]').setInputFiles(asset(file));
  await sleep(500);
  await click(page, page.getByRole("button", { name: /^upload$/i }));
  await expect(row(page, file).getByText(/Ready · \d+ chunks/)).toBeVisible({ timeout: 60000 });
  await sleep(500);
}

export const scenes = {
  "01-intro": (s) => slide(s, "01-intro"),
  "02-architecture": (s) => slide(s, "02-architecture"),

  "03-signin": async (s) => {
    const { page } = s;
    await page.goto(BASE);
    await page.locator("#username").waitFor();
    s.ready();
    await caption(page, "Keycloak · OpenID Connect · authorization code + PKCE");
    await type(page, page.locator("#username"), "alice");
    await type(page, page.locator("#password"), "alice-dev-pw", { delay: 70 });
    await click(page, page.locator("#kc-login"));
    await expect(app(page)).toBeVisible({ timeout: 30000 });
    await caption(page, "alice · role: admin · tenant A");
    await hover(page, page.getByText(/Signed in as/).first(), 1500);
  },

  "04-workspaces": async (s) => {
    const { page } = s;
    await go(page, BASE);
    await expect(page.getByRole("heading", { name: "Workspaces" })).toBeVisible({ timeout: 30000 });
    s.ready();
    await caption(page, "Create workspaces");
    await type(page, page.getByPlaceholder("Name"), "Northwind HR");
    await type(page, page.getByPlaceholder("Description (optional)"), "People policies and handbook");
    await click(page, page.getByRole("button", { name: "Create", exact: true }));
    await expect(page.getByRole("link", { name: "Northwind HR" })).toBeVisible();
    await type(page, page.getByPlaceholder("Name"), "Engineering Wiki");
    await click(page, page.getByRole("button", { name: "Create", exact: true }));
    await expect(page.getByRole("link", { name: "Engineering Wiki" })).toBeVisible();
    await caption(page, "Search");
    await type(page, page.getByPlaceholder("Search…"), "hr");
    await sleep(900);
    await page.getByPlaceholder("Search…").fill("");
    await caption(page, "Rename · optimistic concurrency (version check)");
    await click(page, row(page, "Northwind HR").getByRole("button", { name: "Edit" }));
    // in edit mode the row's text lives in inputs, so find the row by them rather than by its text
    const editing = page.locator("li").filter({ has: page.locator("input") });
    const name = editing.locator("input").first();
    await click(page, name, { pause: 150 });
    await name.fill("");
    await name.pressSequentially(HR, { delay: 55 });
    await click(page, editing.getByRole("button", { name: "Save" }));
    await expect(page.getByRole("link", { name: HR })).toBeVisible();
  },

  "05-members": async (s, ctx) => {
    const { page } = s;
    await go(page, BASE);
    await expect(page.getByRole("link", { name: HR })).toBeVisible({ timeout: 30000 });
    await openWorkspace(page);
    s.ready();
    await caption(page, "Members · roles per workspace");
    await type(page, page.getByPlaceholder("User ID"), ctx.subs.bob);
    await hover(page, page.locator("select").last(), 300);
    await page.locator("select").last().selectOption("Editor");
    await click(page, page.getByRole("button", { name: "Add / update" }));
    await sleep(500);
    await page.getByPlaceholder("User ID").fill("");
    await type(page, page.getByPlaceholder("User ID"), ctx.subs.carol);
    await page.locator("select").last().selectOption("Viewer");
    await click(page, page.getByRole("button", { name: "Add / update" }));
    await caption(page, "Non-members get 404 — they can't tell the workspace exists");
    await sleep(1200);
  },

  "06-upload": async (s) => {
    const { page } = s;
    await go(page, BASE);
    await openWorkspace(page);
    s.ready();
    await caption(page, "Upload · background ingestion · live status over SignalR");
    for (const f of ["handbook.md", "security-policy.md", "contacts.md"]) await upload(page, f);
    await caption(page, "Extract → chunk → embed → store (pgvector + full-text)");
  },

  "07-manage": async (s) => {
    const { page } = s;
    await go(page, BASE);
    await openWorkspace(page);
    s.ready();
    await caption(page, "PDFs work too");
    await upload(page, "travel-policy.pdf");
    await upload(page, "old-notes.md");
    await caption(page, "Search · rename · reprocess · delete");
    await type(page, page.getByPlaceholder("Search documents…"), "travel");
    await sleep(800);
    await page.getByPlaceholder("Search documents…").fill("");
    const old = row(page, "old-notes.md");
    await expect(old).toBeVisible();
    await click(page, old.getByRole("button", { name: "Reprocess" }));
    await expect(old.getByText(/Ready · \d+ chunks/)).toBeVisible({ timeout: 30000 });
    page.once("dialog", (d) => d.accept());
    await click(page, old.getByRole("button", { name: "Delete" }));
    await expect(row(page, "old-notes.md")).toHaveCount(0);
  },

  "08-chat": async (s) => {
    const { page } = s;
    await go(page, `${BASE}/chat`);
    await expect(page.getByRole("heading", { name: "Ask your documents" })).toBeVisible({ timeout: 30000 });
    s.ready();
    await caption(page, "Hybrid retrieval · streamed answer · numbered citations");
    await ask(page, "How many days can I work remotely?", 0);
    await hover(page, page.getByRole("link", { name: "handbook" }).first(), 900);
    await caption(page, "Works across Markdown and PDF sources");
    await ask(page, "How much can I spend on a hotel?", 1);
    await hover(page, page.getByRole("link", { name: /travel-policy/i }).first(), 900);
  },

  "09-feedback": async (s) => {
    const { page } = s;
    await go(page, `${BASE}/chat`);
    await expect(page.getByRole("heading", { name: "Ask your documents" })).toBeVisible({ timeout: 30000 });
    s.ready();
    await ask(page, "How many days can I work remotely?", 0);
    await caption(page, "Thumbs up / down feedback");
    await click(page, page.getByTitle("Helpful", { exact: true }).first());
    await caption(page, "Same question again → semantic cache, no model call");
    await ask(page, "How many days can I work remotely?", 1);
    await expect(page.getByText("⚡ cached").first()).toBeVisible();
    await hover(page, page.getByText("⚡ cached").first(), 1200);
  },

  "10-safety": async (s) => {
    const { page } = s;
    await go(page, `${BASE}/chat`);
    await expect(page.getByRole("heading", { name: "Ask your documents" })).toBeVisible({ timeout: 30000 });
    s.ready();
    await caption(page, "Hidden “ignore all previous instructions” in a document is neutralized");
    await ask(page, "What are the password requirements?", 0);
    await caption(page, "PII in the question is masked before processing");
    await ask(page, "Email carlos.ruiz@example.com a summary of the remote work policy", 1);
    await expect(page.getByText(/masked in your question/)).toBeVisible();
    await hover(page, page.getByText(/masked in your question/).first(), 900);
    await caption(page, "Not in the documents? It says so instead of guessing");
    await ask(page, "Who won the 2018 World Cup?", 2);
  },

  "11-roles": async (s) => {
    const { page } = s;
    await go(page, BASE);
    await expect(page.getByRole("heading", { name: "Workspaces" })).toBeVisible({ timeout: 30000 });
    s.ready();
    await caption(page, "carol · viewer — no create form, no Operations or Audit links");
    await hover(page, page.getByText(/Signed in as/).first(), 1200);
    await openWorkspace(page);
    await caption(page, "No upload, edit or delete controls — and the API enforces it too");
    await sleep(1500);
    await click(page, page.getByRole("link", { name: "Chat" }));
    await caption(page, "Viewers can still ask questions");
    await sleep(1200);
  },

  "12-tenants": async (s) => {
    const { page } = s;
    await go(page, BASE);
    await expect(page.getByRole("heading", { name: "Workspaces" })).toBeVisible({ timeout: 30000 });
    s.ready();
    await caption(page, "dave · admin of a different tenant");
    await hover(page, page.getByText(/Signed in as/).first(), 1200);
    await expect(page.getByText(/No workspaces yet/)).toBeVisible();
    await caption(page, "Sees none of tenant A's data — filters are global and fail closed");
    await sleep(2500);
  },

  "13-ops": async (s) => {
    const { page } = s;
    await go(page, BASE);
    await expect(app(page)).toBeVisible({ timeout: 30000 });
    await click(page, page.getByRole("link", { name: "Operations" }));
    await expect(page.getByRole("heading", { name: "Operations" })).toBeVisible();
    // Start the narration only once the numbers are on screen (a slow first load must not show "Loading…").
    await expect(page.getByText("Latency p95")).toBeVisible({ timeout: 90000 });
    await sleep(600);
    s.ready();
    await caption(page, "Latency percentiles · cost per question · tokens by model · outcomes");
    await sleep(2500);
    await page.mouse.move(500, 300);
    await caption(page, "Per-tenant monthly quota, enforced on every request");
    await page.mouse.wheel(0, 380);
    await sleep(2500);
    await caption(page, "Latest eval scores vs the committed baseline");
    await page.mouse.wheel(0, 700);
    await sleep(2500);
  },

  "14-audit": async (s) => {
    const { page } = s;
    await go(page, BASE);
    await expect(app(page)).toBeVisible({ timeout: 30000 });
    s.ready();
    await click(page, page.getByRole("link", { name: "Audit log" }));
    await expect(page.getByRole("heading", { name: "Audit log" })).toBeVisible();
    await caption(page, "Who changed what, and when");
    await sleep(2500);
  },

  "15-traces": async (s) => {
    const { page } = s;
    await page.goto(`${DASH}/traces`);
    await page.locator("input[type=search]").first().waitFor({ timeout: 30000 });
    await sleep(2500);
    s.ready();
    await caption(page, "OpenTelemetry traces · Aspire dashboard");
    // The root span of a chat request is the HTTP route; filtering on it hides the background polling traces.
    await type(page, page.locator("input[type=search]").first(), "messages", { delay: 80 });
    await sleep(2500);
    await click(page, page.locator("[role=row]").nth(1), { pause: 1200 });
    await caption(page, "One chat request: HTTP → chat.ask → embed · retrieve · LLM stream · SQL");
    await sleep(2500);
    await page.mouse.wheel(0, 250);
    await sleep(2500);
  },

  "16-quality": (s) => slide(s, "16-quality"),
  "17-outro": (s) => slide(s, "17-outro"),
};
