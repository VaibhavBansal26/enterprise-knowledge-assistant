// Shared helpers for recording the demo: a visible cursor, on-screen captions, and timing tied to the narration.
import { mkdirSync, existsSync, readFileSync, renameSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { chromium, expect as baseExpect } from "../tests/e2e/node_modules/@playwright/test/index.mjs";

// Keycloak and the API can be slow on a laptop (the Keycloak image is emulated on Apple silicon): be patient.
export const expect = baseExpect.configure({ timeout: 45000 });
export { chromium };
export const ROOT = path.dirname(fileURLToPath(import.meta.url));
export const OUT = path.join(ROOT, "out");
export const BASE = process.env.DEMO_URL ?? "http://localhost:5173";
export const KC = process.env.KEYCLOAK_URL ?? "http://localhost:8081";
export const DASH = process.env.DASHBOARD_URL ?? "http://localhost:18888";
export const VIEWPORT = { width: 1024, height: 576 }; // recorded at 1280x720, so text appears 25% larger
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export const USERS = {
  alice: { password: "alice-dev-pw" },
  bob: { password: "bob-dev-pw" },
  carol: { password: "carol-dev-pw" },
  dave: { password: "dave-dev-pw" },
};

const INIT = `(() => {
  const css = \`
    #__kb_cursor{position:fixed;z-index:2147483647;width:22px;height:22px;margin:-4px 0 0 -4px;pointer-events:none;
      transition:transform 60ms linear;display:none}
    #__kb_cursor svg{filter:drop-shadow(0 1px 2px rgba(0,0,0,.45))}
    .__kb_ripple{position:fixed;z-index:2147483646;width:14px;height:14px;margin:-7px 0 0 -7px;border-radius:50%;
      border:3px solid #2563eb;pointer-events:none;animation:__kb_r .55s ease-out forwards}
    @keyframes __kb_r{to{transform:scale(3.2);opacity:0}}
    #__kb_caption{position:fixed;z-index:2147483645;left:50%;bottom:22px;transform:translateX(-50%);max-width:86%;
      padding:9px 18px;border-radius:10px;background:rgba(17,24,39,.88);color:#fff;font:600 17px/1.35 system-ui,sans-serif;
      text-align:center;pointer-events:none;display:none;box-shadow:0 4px 18px rgba(0,0,0,.35)}\`;
  const mount = () => {
    if (document.getElementById('__kb_cursor')) return;
    const st = document.createElement('style'); st.textContent = css; document.documentElement.appendChild(st);
    const c = document.createElement('div'); c.id = '__kb_cursor';
    c.innerHTML = '<svg width="22" height="22" viewBox="0 0 22 22"><path d="M3 2l14 8-6 1.5L8.5 18z" fill="#fff" stroke="#111" stroke-width="1.4" stroke-linejoin="round"/></svg>';
    document.documentElement.appendChild(c);
    const cap = document.createElement('div'); cap.id = '__kb_caption'; document.documentElement.appendChild(cap);
    const t = sessionStorage.getItem('__kb_cap'); if (t) { cap.textContent = t; cap.style.display = 'block'; }
    const p = sessionStorage.getItem('__kb_pos'); if (p) { const [x, y] = p.split(','); c.style.left = x + 'px'; c.style.top = y + 'px'; c.style.display = 'block'; }
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount); else mount();
  window.addEventListener('mousemove', (e) => {
    mount(); const c = document.getElementById('__kb_cursor'); c.style.display = 'block';
    c.style.left = e.clientX + 'px'; c.style.top = e.clientY + 'px';
    try { sessionStorage.setItem('__kb_pos', e.clientX + ',' + e.clientY); } catch {}
  }, true);
  window.addEventListener('mousedown', (e) => {
    mount(); const r = document.createElement('div'); r.className = '__kb_ripple';
    r.style.left = e.clientX + 'px'; r.style.top = e.clientY + 'px'; document.documentElement.appendChild(r);
    setTimeout(() => r.remove(), 700);
  }, true);
  window.__kb_caption = (t) => {
    mount(); const cap = document.getElementById('__kb_caption');
    try { t ? sessionStorage.setItem('__kb_cap', t) : sessionStorage.removeItem('__kb_cap'); } catch {}
    cap.textContent = t || ''; cap.style.display = t ? 'block' : 'none';
  };
})();`;

export async function caption(page, text) {
  await page.evaluate((t) => window.__kb_caption && window.__kb_caption(t), text).catch(() => {});
}

export async function click(page, locator, { pause = 450 } = {}) {
  await locator.scrollIntoViewIfNeeded();
  const b = await locator.boundingBox();
  if (b) await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 28 });
  await sleep(pause * 0.6);
  await locator.click();
  await sleep(pause);
}

export async function type(page, locator, text, { delay = 55 } = {}) {
  await click(page, locator, { pause: 200 });
  await locator.pressSequentially(text, { delay });
  await sleep(250);
}

export async function hover(page, locator, pause = 700) {
  await locator.scrollIntoViewIfNeeded();
  const b = await locator.boundingBox();
  if (b) await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2, { steps: 28 });
  await sleep(pause);
}

/** Opens a recorded scene. `ready()` marks where the narration starts; everything before it is trimmed off. */
export async function openScene(browser, id, { storageState } = {}) {
  mkdirSync(path.join(OUT, "video"), { recursive: true });
  const ctx = await browser.newContext({
    viewport: VIEWPORT,
    recordVideo: { dir: path.join(OUT, "video", ".raw"), size: { width: 1280, height: 720 } },
    storageState,
  });
  await ctx.addInitScript(INIT);
  const created = Date.now();
  const page = await ctx.newPage();
  const scene = { id, ctx, page, created, readyAt: null };
  scene.ready = () => { scene.readyAt = Date.now(); };
  /** Waits until `seconds` have passed since ready(), so the scene lasts as long as its narration. */
  scene.holdUntil = async (seconds) => {
    const left = scene.readyAt + seconds * 1000 - Date.now();
    if (left > 0) await sleep(left);
    return left;
  };
  return scene;
}

export async function closeScene(scene, { target }) {
  const offset = ((scene.readyAt ?? Date.now()) - scene.created) / 1000;
  const elapsed = (Date.now() - (scene.readyAt ?? Date.now())) / 1000;
  const video = scene.page.video();
  await scene.page.close();
  const raw = await video.path();
  await scene.ctx.close();
  const dest = path.join(OUT, "video", `${scene.id}.webm`);
  renameSync(raw, dest);
  writeFileSync(path.join(OUT, "video", `${scene.id}.json`), JSON.stringify({ offset, elapsed, target }));
  return { offset, elapsed };
}

export function durations() {
  const f = path.join(OUT, "durations.json");
  return existsSync(f) ? JSON.parse(readFileSync(f, "utf8")) : {};
}

export const narration = () => JSON.parse(readFileSync(path.join(ROOT, "narration.json"), "utf8")).scenes;

/** Target length of a scene: the real narration if it has been generated, else an estimate from the word count. */
export function targetFor(scene, known) {
  return known[scene.id] ?? scene.text.split(/\s+/).length / 2.5 + 1.2;
}

// ---- Keycloak / API helpers ----
export async function keycloakLogin(page, user) {
  await page.locator("#username").fill(user);
  await page.locator("#password").fill(USERS[user].password);
  await page.locator("#kc-login").click();
}

export async function userSub(user) {
  const res = await fetch(`${KC}/realms/kb/protocol/openid-connect/token`, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "password", client_id: "kb-dev-cli", username: user, password: USERS[user].password }),
  });
  const token = (await res.json()).access_token;
  return JSON.parse(Buffer.from(token.split(".")[1], "base64url").toString()).sub;
}

/** Signs in once, unrecorded, and keeps the Keycloak session so later scenes open already signed in. */
export async function sessionFor(browser, user) {
  const file = path.join(OUT, `state-${user}.json`);
  for (let attempt = 1; ; attempt++) {
    const ctx = await browser.newContext({ viewport: VIEWPORT });
    try {
      const page = await ctx.newPage();
      await page.goto(BASE);
      await page.locator("#username").waitFor({ timeout: 90000 });
      await keycloakLogin(page, user);
      await expect(page.getByRole("heading", { name: "Knowledge Assistant" })).toBeVisible({ timeout: 90000 });
      await ctx.storageState({ path: file });
      return file;
    } catch (e) {
      if (attempt >= 3) throw e;
      console.log(`  sign-in for ${user} failed (attempt ${attempt}), retrying`);
    } finally {
      await ctx.close();
    }
  }
}
