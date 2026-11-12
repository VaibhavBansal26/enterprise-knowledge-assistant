# Roadmap

About 10 weeks part-time. Each phase ends with something demoable.

## Phase 0: Foundations (Week 1)
- Solution layout: Domain, Application, Infrastructure, Api, Tests
- Docker Compose: Postgres (pgvector) + Redis
- Options pattern with startup validation, Serilog, health checks, Swagger
- GitHub Actions: build, test, lint
- Vite + React + TS shell with typed API client from OpenAPI

**Done when:** `docker compose up` gives a healthy API and UI, CI is green.

## Phase 1: CRUD and data layer (Weeks 2-3)
- Entities: Workspace, Document, Conversation, Message
- EF Core migrations, soft delete (global query filter), `xmin` optimistic concurrency
- Versioned endpoints (`/api/v1`): paging, filtering, sorting, PATCH
- DTOs, FluentValidation, ProblemDetails
- React list/detail/forms with TanStack Query
- Unit + Testcontainers integration tests

**Done when:** full CRUD works end to end, with tests.

## Phase 2: Middleware and cross-cutting (Week 4)
- Exception handler, correlation ID, request logging with redaction
- CORS and security headers, Redis-backed rate limiting
- Idempotency (`Idempotency-Key` in Redis)
- Output caching, compression, endpoint filters
- Architecture tests (NetArchTest)

**Done when:** every request is traceable by correlation ID, duplicate uploads are idempotent, rate-limit test passes.

## Phase 3: Auth and multi-tenancy (Week 5)
- OIDC/JWT (Keycloak in Compose), refresh flow, React login
- Roles (Admin/Editor/Viewer), policy and resource-based authorization
- Tenant-resolution middleware, EF global filters per tenant
- Audit log

**Done when:** tests prove user A cannot read user B's workspace.

## Phase 4: Ingestion pipeline (Weeks 6-7)
- Upload with size/MIME limits, blob storage (local or MinIO)
- Outbox pattern, Hangfire/MassTransit workers
- Parse (PDF, DOCX, MD), chunk, embed into pgvector (HNSW)
- Job status via SignalR, retries, dead-letter, Redis locks
- React upload with progress and ingestion status

**Done when:** a 100-page PDF is processed in the background without blocking the API.

## Phase 5: LLM and RAG (Weeks 7-8)
- `ILlmClient` / `IEmbeddingClient` abstractions (`Microsoft.Extensions.AI`)
- Hybrid search (vector + full-text), permission-filtered in SQL
- Versioned prompts, citations, structured outputs
- Streaming (SSE/SignalR) with `CancellationToken` end to end
- Redis semantic cache, conversation memory with token budget
- Polly: retry with jitter, circuit breaker, timeout, 429 handling
- React chat UI with streaming and citations panel

**Done when:** streamed, cited, permission-aware answers; repeat questions hit the cache.

## Phase 6: Quality, safety, observability (Week 9)
- Eval harness with golden Q&A set, run in CI
- Prompt-injection defenses, PII redaction, grounding check
- OpenTelemetry traces/metrics, Sentry
- Usage and cost ledger, per-tenant quotas

**Done when:** a dashboard shows p95 latency, cost per query, eval scores; regressions fail CI.

## Phase 7: Hardening and delivery (Week 10)
- Multi-stage Dockerfiles, EF migrations bundle
- k6 load tests, Playwright E2E
- Deploy (Render / Fly.io / Azure Container Apps)
- Runbook, architecture diagram, ADR log

**Done when:** live at a URL with documented deploy and rollback.

## Stretch
- Connectors (Google Drive, SharePoint, Confluence)
- Admin analytics (unanswered questions, top sources)
- Multi-model routing with fallback
- Feedback-driven ranking

## Risks
| Risk | Mitigation |
|---|---|
| Ingestion format scope creep | Start with PDF and Markdown |
| Poor answer quality | Build eval set in Phase 5 |
| Permission leaks in retrieval | Filter in the SQL query, not after |
| LLM cost surprises | Quotas, caching, usage ledger from day one |
