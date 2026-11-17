// Records every scene against the running stack. Usage:  node record.mjs [sceneId ...]
// Run on a freshly reset database (scenes create the workspace and upload the documents the later scenes use).
import { chromium, closeScene, durations, narration, openScene, sessionFor, targetFor, userSub, OUT } from "./lib.mjs";
import { scenes } from "./scenes.mjs";
import { mkdirSync, rmSync } from "node:fs";
import path from "node:path";

const only = process.argv.slice(2);
const list = narration().filter((s) => only.length === 0 || only.includes(s.id));
const known = durations();

mkdirSync(path.join(OUT, "video"), { recursive: true });
if (only.length === 0) rmSync(path.join(OUT, "video"), { recursive: true, force: true });

const browser = await chromium.launch();
const states = {};
for (const u of ["alice", "carol", "dave"]) states[u] = await sessionFor(browser, u);
const subs = { bob: await userSub("bob"), carol: await userSub("carol") };

const who = { "11-roles": "carol", "12-tenants": "dave" };
for (const sc of list) {
  const target = targetFor(sc, known);
  const useState = sc.id === "03-signin" ? undefined : states[who[sc.id] ?? "alice"];
  const scene = await openScene(browser, sc.id, { storageState: useState });
  let failed = null;
  try {
    await scenes[sc.id](scene, { subs, browser });
    await scene.holdUntil(target);
  } catch (e) {
    failed = e;
    await scene.page.screenshot({ path: path.join(OUT, `fail-${sc.id}.png`) }).catch(() => {});
  }
  const r = await closeScene(scene, { target });
  const flag = r.elapsed > target + 0.5 ? `  ⚠ actions ran ${(r.elapsed - target).toFixed(1)}s longer than the narration` : "";
  console.log(`${failed ? "✘" : "✓"} ${sc.id}  actions ${r.elapsed.toFixed(1)}s  target ${target.toFixed(1)}s${flag}`);
  if (failed) console.log("   ", String(failed.message).split("\n")[0]);
}
await browser.close();
