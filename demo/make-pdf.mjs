import { fileURLToPath } from "node:url";
import { chromium } from "../tests/e2e/node_modules/@playwright/test/index.mjs";
const b = await chromium.launch();
const p = await b.newPage();
await p.goto("file:///tmp/travel.html");
await p.pdf({ path: fileURLToPath(new URL("./assets/travel-policy.pdf", import.meta.url)), format: "A4" });
await b.close();
