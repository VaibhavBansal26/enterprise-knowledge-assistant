// Full-screen title cards rendered as HTML (no app needed). Numbers on the quality slide are measured results from this project.
const base = `
  *{box-sizing:border-box;margin:0}
  body{width:1024px;height:576px;font-family:Inter,system-ui,-apple-system,Segoe UI,sans-serif;color:#e5e7eb;
    background:radial-gradient(1200px 600px at 20% 0%,#1e3a8a 0%,#0f172a 55%,#020617 100%);display:flex;align-items:center;justify-content:center}
  .wrap{width:900px}
  h1{font-size:52px;line-height:1.1;letter-spacing:-1px;color:#fff}
  h2{font-size:34px;color:#fff;margin-bottom:22px;letter-spacing:-.5px}
  p.sub{font-size:21px;color:#93c5fd;margin-top:14px}
  .chips{display:flex;flex-wrap:wrap;gap:10px;margin-top:30px}
  .chip{padding:8px 16px;border-radius:999px;background:rgba(147,197,253,.12);border:1px solid rgba(147,197,253,.35);font-size:17px;color:#dbeafe}
  .grid{display:grid;grid-template-columns:repeat(3,1fr);gap:16px}
  .card{padding:18px 20px;border-radius:14px;background:rgba(255,255,255,.06);border:1px solid rgba(255,255,255,.12)}
  .card b{display:block;font-size:34px;color:#fff;letter-spacing:-1px}
  .card span{font-size:15px;color:#a5b4fc}
  .url{margin-top:26px;font:600 24px ui-monospace,Menlo,monospace;color:#fbbf24}
  svg text{font-family:Inter,system-ui,sans-serif}
`;
const page = (body) => `<!doctype html><meta charset="utf-8"><style>${base}</style><body>${body}</body>`;

export const slides = {
  "01-intro": page(`<div class="wrap">
    <p class="sub" style="margin:0 0 12px">Production-grade RAG</p>
    <h1>Enterprise Knowledge Assistant</h1>
    <p class="sub">Ask your company's documents. Get cited, streamed answers.</p>
    <div class="chips"><span class="chip">.NET 9</span><span class="chip">React + TypeScript</span><span class="chip">PostgreSQL + pgvector</span>
      <span class="chip">Redis</span><span class="chip">LLM behind an interface</span><span class="chip">Keycloak (OIDC)</span><span class="chip">OpenTelemetry</span></div></div>`),

  "02-architecture": page(`<svg width="1024" height="576" viewBox="0 0 1024 576">
    <defs><marker id="a" markerWidth="10" markerHeight="8" refX="9" refY="4" orient="auto"><path d="M0,0 L10,4 L0,8z" fill="#93c5fd"/></marker></defs>
    <text x="48" y="58" fill="#fff" font-size="32" font-weight="700">Architecture</text>
    <g fill="rgba(255,255,255,.07)" stroke="rgba(147,197,253,.5)" stroke-width="1.5">
      <rect x="48" y="150" width="190" height="86" rx="14"/><rect x="330" y="140" width="250" height="110" rx="14"/>
      <rect x="690" y="96" width="290" height="92" rx="14"/><rect x="690" y="206" width="290" height="92" rx="14"/>
      <rect x="690" y="316" width="290" height="92" rx="14"/><rect x="330" y="330" width="250" height="78" rx="14"/>
      <rect x="48" y="330" width="190" height="78" rx="14"/><rect x="330" y="450" width="250" height="78" rx="14"/></g>
    <g fill="#fff" font-size="19" font-weight="600" text-anchor="middle">
      <text x="143" y="190">React SPA</text><text x="455" y="186">ASP.NET Core API</text>
      <text x="835" y="132">PostgreSQL 17</text><text x="835" y="242">Redis</text><text x="835" y="352">LLM</text>
      <text x="455" y="364">Keycloak</text><text x="143" y="364">Browser sign-in</text><text x="455" y="484">Observability</text></g>
    <g fill="#a5b4fc" font-size="14" text-anchor="middle">
      <text x="143" y="214">Vite · TanStack Query</text><text x="455" y="210">Clean architecture · Minimal APIs</text><text x="455" y="230">SSE streaming · SignalR</text>
      <text x="835" y="156">pgvector · full-text · outbox queue</text><text x="835" y="266">rate limit · idempotency · locks · cache</text>
      <text x="835" y="376">Claude · offline stand-in (demo)</text><text x="455" y="388">OIDC · JWT · roles · tenants</text>
      <text x="143" y="388">code flow + PKCE</text><text x="455" y="508">OpenTelemetry · Aspire · Sentry</text></g>
    <g stroke="#93c5fd" stroke-width="2" fill="none" marker-end="url(#a)">
      <path d="M238 193 H328"/><path d="M580 170 H688"/><path d="M580 195 C640 220 640 240 688 250"/><path d="M580 225 C640 300 640 350 688 362"/>
      <path d="M455 250 V328"/><path d="M143 330 V240"/><path d="M455 408 V448"/></g></svg>`),

  "16-quality": page(`<div class="wrap"><h2>Built to be operated</h2><div class="grid">
    <div class="card"><b>300</b><span>automated tests · real Postgres + Redis via Testcontainers</span></div>
    <div class="card"><b>CI gate</b><span>golden-set evals fail the build on a regression</span></div>
    <div class="card"><b>0 failures</b><span>k6 run: 1,369 requests, read p95 33 ms</span></div>
    <div class="card"><b>4 journeys</b><span>Playwright: sign-in, upload, cited answer, roles, tenants</span></div>
    <div class="card"><b>3 images</b><span>API · migrations bundle · web, with a separate migrate step</span></div>
    <div class="card"><b>Runbook</b><span>deploy, rollback, incidents · 6 decision records</span></div></div></div>`),

  "17-outro": page(`<div class="wrap"><h1>Thanks for watching</h1>
    <p class="sub">Open source · free deployment config for Render, Neon and Auth0 included</p>
    <p class="url">github.com/VaibhavBansal26/enterprise-knowledge-assistant</p></div>`),
};
