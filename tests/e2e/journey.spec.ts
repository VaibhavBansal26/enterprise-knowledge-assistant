import { expect, test, type Page } from "@playwright/test";

// Sample users from infra/keycloak/kb-realm.json. Passwords default to the dev realm's; a hosted deployment generates
// its own, so pass E2E_PASSWORD_ALICE etc. there.
const ALICE = { user: "alice", password: process.env.E2E_PASSWORD_ALICE ?? "alice-dev-pw" }; // admin
const CAROL = { user: "carol", password: process.env.E2E_PASSWORD_CAROL ?? "carol-dev-pw" }; // viewer (read-only)
const DAVE = { user: "dave", password: process.env.E2E_PASSWORD_DAVE ?? "dave-dev-pw" }; // admin of a different tenant

async function signIn(page: Page, who: { user: string; password: string }) {
  await page.goto("/");
  await page.locator("#username").fill(who.user);
  await page.locator("#password").fill(who.password);
  await page.locator("#kc-login").click();
  await expect(page.getByRole("heading", { name: "Knowledge Assistant" })).toBeVisible();
}

test("admin signs in, uploads a document, asks a question and gets a cited answer", async ({ page }) => {
  const workspace = `e2e-${Date.now()}`;
  await signIn(page, ALICE);

  await page.getByPlaceholder("Name").fill(workspace);
  await page.getByRole("button", { name: /create/i }).click();
  await page.getByRole("link", { name: workspace }).click();

  await page.locator('input[type="file"]').setInputFiles({
    name: "policy.md",
    mimeType: "text/markdown",
    buffer: Buffer.from("# Remote work\nEmployees may work remotely up to three days per week with manager approval.\n"),
  });
  await page.getByRole("button", { name: /upload/i }).click();
  // Ingestion runs in the background; the row flips to Ready when chunks are embedded.
  await expect(page.getByText(/ready/i).first()).toBeVisible({ timeout: 45_000 });

  await page.getByRole("link", { name: "Chat" }).click();
  await page.getByPlaceholder("Ask a question about your documents…").fill("How many days can employees work remotely?");
  await page.getByRole("button", { name: /send|ask/i }).click();

  const answer = page.getByText(/three days per week/i).first();
  await expect(answer).toBeVisible({ timeout: 30_000 });
  // The answer lists the documents it was grounded in, each linking back to its source.
  await expect(page.getByRole("link", { name: "policy", exact: true }).first()).toBeVisible();
});

test("a viewer cannot administer: no Operations or Audit links", async ({ page }) => {
  await signIn(page, CAROL);
  await expect(page.getByRole("link", { name: "Workspaces" })).toBeVisible();
  await expect(page.getByRole("link", { name: "Operations" })).toHaveCount(0);
  await expect(page.getByRole("link", { name: "Audit log" })).toHaveCount(0);
});

test("another tenant's admin cannot see this tenant's workspaces", async ({ page }) => {
  await signIn(page, DAVE);
  await expect(page.getByRole("heading", { name: "Workspaces" })).toBeVisible();
  await expect(page.getByRole("link", { name: /^e2e-\d+$/ })).toHaveCount(0);
});

test("the admin sees the Ops dashboard", async ({ page }) => {
  await signIn(page, ALICE);
  await page.getByRole("link", { name: "Operations" }).click();
  await expect(page.getByText(/p95/i).first()).toBeVisible();
});
