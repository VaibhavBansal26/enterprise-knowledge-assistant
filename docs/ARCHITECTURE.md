# Architecture

```mermaid
flowchart LR
  Browser["Browser (React SPA)"] -->|HTTPS| Web["web: nginx<br/>static files + reverse proxy"]
  Browser -->|OIDC code + PKCE| KC["Keycloak / any OIDC provider"]
  Web -->|/api, /hubs, /health| API["api: ASP.NET Core 9"]
  API -->|validate JWT (JWKS)| KC
  API --> PG[("PostgreSQL 17<br/>+ pgvector, FTS, outbox queue")]
  API --> RD[("Redis<br/>rate limit, idempotency,<br/>locks, semantic cache, SignalR")]
  API -->|embeddings, chat| LLM["LLM provider<br/>(Anthropic / OpenAI-compatible / offline stand-ins)"]
  API -->|OTLP| OBS["Aspire / any OTLP backend"]
  API -->|errors| SENTRY["Sentry"]
  MIG["migrate: EF bundle<br/>(runs once per release)"] --> PG
```

## Request path for a chat question
1. `POST /api/v1/conversations/{id}/messages` passes auth, tenant resolution, rate limit, and the per-workspace quota.
2. Input is PII-masked; the question is embedded; the semantic cache is checked.
3. Hybrid retrieval (vector + full-text, fused with Reciprocal Rank Fusion) runs in one SQL query that also applies tenant and workspace permissions.
4. Retrieved text has injection phrases neutralised, then the prompt is built within a token budget.
5. The LLM streams back over SSE; the answer is checked for prompt leakage and scored for grounding.
6. Tokens and estimated cost are written to the usage ledger; spans and metrics are emitted.

## Ingestion
Upload stores the blob and enqueues a job in a Postgres outbox table (`FOR UPDATE SKIP LOCKED`, leases, backoff, dead letter). A background processor extracts text, chunks, embeds, and writes chunks; progress is pushed to the UI over SignalR (Redis backplane).

## Layers
`Domain` (entities) ← `Application` (contracts, pure logic: safety, pricing, chunking) ← `Infrastructure` (EF Core, Redis, LLM, storage) ← `Api` (endpoints, middleware). An architecture test enforces the dependency direction.

Decisions and their reasons: [adr/](adr/). Operations: [RUNBOOK.md](RUNBOOK.md).
