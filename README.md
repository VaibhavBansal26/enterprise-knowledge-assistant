# Enterprise Knowledge Assistant

A production-grade RAG application: employees ask questions over internal documents and get streamed, cited answers that respect per-user permissions.

**Stack:** .NET 9 (Minimal APIs, EF Core) · React + TypeScript (Vite) · PostgreSQL + pgvector · Redis · LLM provider behind an interface

## Status
Phases 0 (foundations), 1 (CRUD), 2 (middleware), 3 (auth and multi-tenancy), 4 (ingestion) and 5 (RAG chat) done. See [ROADMAP.md](ROADMAP.md).

## Quick start
```bash
# Postgres (pgvector) on :5433, Redis on :6380, Keycloak on :8081
# (non-default host ports avoid clashing with locally installed services;
#  override with POSTGRES_PORT / REDIS_PORT)
docker compose up -d

# API on :8080 (needs .NET 9 SDK); applies EF migrations in Development.
# Needs Keycloak up: the API fetches its signing keys from it.
ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/Api

# Web on :5173 (proxies /api and /health to :8080)
cd web && npm install && npm run dev
```
Run everything in containers: `docker compose --profile full up --build` (API on :8080).

## API (v1)
| Resource | Endpoints |
|---|---|
| Workspaces | `POST/GET /api/v1/workspaces`, `GET/PUT/DELETE /api/v1/workspaces/{id}` |
| Documents | `POST/GET /api/v1/workspaces/{workspaceId}/documents`, `GET/PUT/DELETE .../{id}` |

- Lists take `page`, `pageSize` (max 100), `q` (case-insensitive search) and `sort` (`name`, `title`, `createdAt`; prefix `-` for descending).
- Deletes are soft. Updates use optimistic concurrency: send the `version` you read, get `409` if it is stale.
- Errors are RFC 7807 ProblemDetails; validation failures return `400` with per-field errors.
- Health: `/health/live` (process up), `/health/ready` (Postgres + Redis reachable).

## Ingestion pipeline
Upload a PDF, DOCX, Markdown or text file and it is parsed, chunked, embedded and stored in pgvector.

```
POST /documents (multipart)          worker (BackgroundService)
  validate → store blob → one DB     claim (SKIP LOCKED) → Redis lock → parse → chunk
  transaction: document + outbox  ─▶ → embed → replace chunks → Ready ─▶ SignalR push
```

| Concern | Behaviour |
|---|---|
| Upload checks | Size limit (`Storage:MaxUploadBytes`, default 25 MB, 413), extension allow-list (415), **content signature must match the type** (a renamed executable is rejected, 400), empty files rejected, directory parts of the file name dropped. Files are stored under random keys, so a file name can never reach the file system. |
| Transactional outbox | The document row and its ingestion job are written in **one transaction**, so an upload can never be saved yet never processed. If the save fails the stored file is removed. |
| Queue | The `outbox_messages` table is the queue. Workers claim with `FOR UPDATE SKIP LOCKED` and a time-limited lease, so any number of instances can run concurrently without double-processing, and a crashed worker's claim simply expires. At-least-once delivery; processing is idempotent (chunks are *replaced*). |
| Retries | Transient failures retry with exponential backoff and jitter (`Ingestion:RetryBaseSeconds`, `MaxAttempts`). Permanent ones (corrupt file, no text) fail the document immediately without burning retries. |
| Dead letter | When attempts run out the message is parked (`dead_at`, `last_error`) and the document shows **Failed** with a reason. **Reprocess** (`POST .../documents/{id}/reprocess`) re-queues it. |
| Redis lock | One worker per document at a time, with a compare-and-delete release so an expired lock is never released by its old owner. |
| Search | Chunks hold a 384-d `vector` with an **HNSW cosine index**. Chunking is paragraph-then-sentence aware with overlap (`Ingestion:ChunkSizeChars`, `ChunkOverlapChars`). |
| Live status | SignalR hub at `/hubs/ingestion` (Redis backplane, so a worker on one instance reaches a browser on another). Subscribing needs read access to the workspace (same rules as the REST API). The UI shows upload progress, live status, and falls back to polling if the socket is down. |
| Deleting | Soft-deletes the document and removes its chunks and the stored file. |

Notes:
- **Embeddings are a deterministic hashing stand-in** (`HashingEmbeddingClient`): offline, no API key, similar *wording* scores close, but it has no semantic understanding. Phase 5 swaps in a real model behind `IEmbeddingClient`.
- The worker runs inside the API process by default. Set `Ingestion:WorkerEnabled=false` on API-only instances to run workers separately.
- The queue is Postgres rather than Hangfire/MassTransit on purpose: Postgres is already the system of record, so enqueueing is transactional (no dual write to a second system) and there is one fewer moving part. The `IIngestionNotifier`, `IBlobStorage`, `IDistributedLock` and `IEmbeddingClient` seams keep these choices swappable.
- Blob storage is local disk (`Storage:RootPath`, a volume in Compose). The `IBlobStorage` interface is the place to add S3/MinIO.

## Chat (RAG)
Ask questions in the **Chat** page; answers stream in with numbered citations you can check against the source passages.

```
question → embed → [semantic cache?] → hybrid retrieval (permissions in SQL) → budgeted prompt → LLM (streamed)
        → extract citations → save → SSE events: sources · delta… · done | error
```

| Concern | Behaviour |
|---|---|
| Retrieval | One SQL statement merges **pgvector cosine search** and **Postgres full-text** (Reciprocal Rank Fusion). The permission rules live *inside* the query: tenant, workspace membership (admins: whole tenant), deleted workspaces/documents, document fully ingested, and embedded by the current model. Nothing is filtered afterwards in app code. |
| Nothing relevant | Says so **without calling the model** (no cost, no invitation to guess). |
| Prompt | Versioned (`rag-v1`, stored with every answer). Sources are XML-escaped and delimited, and the system prompt tells the model to treat them as data, so text in a document cannot close its block or inject instructions. |
| Citations | The model writes `[n]`; the server keeps only markers that point at a real source, so invented numbers are dropped. |
| Memory | Earlier turns are sent newest-first up to `Chat:HistoryTokenBudget`; retrieved passages up to `Chat:ContextTokenBudget` (approximate token counts). |
| Semantic cache | Redis. A new first question that is near-identical (cosine ≥ `Chat:CacheSimilarityThreshold`) reuses an earlier answer. Invalidated whenever the tenant's documents change; a hit is **re-checked against the asking user's permissions** for every source behind it, so a cached answer is never shown to someone who could not read it. Follow-up questions are never cached. |
| Streaming | Server-Sent Events over `POST /api/v1/conversations/{id}/messages` (`sources`, `delta`, `done`, `error`). Pressing **Stop** aborts the request and the cancellation reaches the model call; no partial answer is saved. |
| Privacy | Conversations are private to their owner, even from tenant admins. |
| Limits | Own per-user rate limit (`Chat:RequestsPerMinute`, default 20) because each message calls a paid model. |
| Feedback | 👍/👎 per answer (`PUT .../messages/{id}/feedback`). |

### Using Claude (optional)
By default an **offline extractive model** quotes the best-matching passages (free, no key, not a real LLM). To use Claude, set these as environment variables or user secrets, never in a file:

```bash
Llm__Provider=anthropic
Llm__Anthropic__ApiKey=<your key>
# optional: Llm__Anthropic__Model=claude-opus-5-5  Llm__Anthropic__Effort=low
```
Calls go through the official Anthropic SDK. The SDK retries 408/429/5xx (honouring `Retry-After`); a Polly **circuit breaker and first-event timeout** sit on top so an outage fails fast with a clear message. **Server-side refusal fallback** is on by default (`Llm:Anthropic:RefusalFallbackModel`, clear it to disable).

Embeddings are a deterministic **hashing stand-in** by default. For real semantic search point `Embeddings:Provider=openai` at any OpenAI-compatible `/embeddings` endpoint (`Embeddings__OpenAi__ApiKey`, `BaseUrl`, `Model`; must support the `dimensions` parameter, vectors are 384-d). It has its own Polly pipeline (retry with jitter honouring 429, circuit breaker, timeouts). Vectors from different models are not comparable, so chunks record their model and search ignores the others: **reprocess documents after switching**.

## Authentication and multi-tenancy
Sign-in is OpenID Connect (Authorization Code + PKCE) against **Keycloak**; the API only validates bearer JWTs
(issuer, audience `kb-api`, signature, lifetime). The local realm is imported from
[`infra/keycloak/kb-realm.json`](infra/keycloak/kb-realm.json) and is **for development only**
(`start-dev`, throwaway admin, fixed passwords).

| Dev user | Tenant | Role |
|---|---|---|
| `alice` | A | admin |
| `bob` | A | editor |
| `carol` | A | viewer |
| `dave` | B | admin |

Passwords are in the realm file. The Keycloak admin console is at http://localhost:8081.

| Concern | Behaviour |
|---|---|
| Tenant | Taken from the token's `tenant_id` claim by middleware. A token without one is refused (403). Every query is filtered by tenant in EF Core (global query filters), and a context with no tenant matches **no** rows. |
| Roles | `admin` (whole tenant, manages members, deletes workspaces, reads audit log), `editor` (creates and edits), `viewer` (read-only). The token's role is a ceiling that workspace membership cannot raise. |
| Workspace access | Non-admins only see workspaces they are a member of (creators are added automatically). A workspace you cannot read returns **404**, not 403, so its existence is not revealed. Members are managed by admins at `/api/v1/workspaces/{id}/members`. |
| Audit log | Every create/update/delete of workspaces, documents and members is recorded in the same transaction: who, what, when, correlation ID, and the *names* (never values) of changed fields. Admins read it at `/api/v1/audit`. |
| Secure by default | Everything requires a token except `/health/*`, `/api/v1/ping` and OpenAPI. |
| Rate limits / idempotency | Keyed per user (per IP when anonymous). Refused (401/403) requests are never stored for replay. |
| Web client | Tokens are kept **in memory only**; a reload re-authenticates silently through the Keycloak session. Access tokens are renewed with the refresh token. The UI hides what a role cannot do, but the API is what enforces it. |

## Cross-cutting behaviour
Request pipeline, in order: correlation ID → exception handling → status-code pages → request logging → security headers → compression → CORS → authentication → tenant → rate limiting → authorization → idempotency → endpoints.

| Concern | Behaviour |
|---|---|
| Correlation ID | Reuses a well-formed inbound `X-Correlation-Id`, otherwise generates one; echoed on the response, attached to every log line and to ProblemDetails (`correlationId`). |
| Errors | Unhandled exceptions return a generic 500 ProblemDetails (details only in logs). Stale updates return 409. |
| Logging | Serilog request logging with sensitive query values (`token`, `key`, `password`, ...) masked. Health probes log at Verbose. |
| Security headers | `nosniff`, frame denial, `no-referrer`, locked-down CSP; HSTS outside Development. |
| CORS | Allow-list from `Cors:AllowedOrigins` (default `http://localhost:5173`). |
| Rate limiting | Fixed window per client IP, stored in Redis so it holds across instances (`RateLimiting:*`). 429 with `Retry-After`. Health probes exempt. Fails open if Redis is down. |
| Idempotency | `POST` with `Idempotency-Key` replays the first response (`Idempotent-Replayed: true`); same key with a different body gets 422; a concurrent duplicate gets 409; 5xx responses are not stored. Fails closed (503) if Redis is down. |
| Limits | Request bodies capped at 1 MB (upload endpoints will raise this in Phase 4). |

## Tests
```bash
dotnet test   # integration tests start throwaway Postgres and Redis via Testcontainers, so Docker must be running
```

## Migrations
```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add <Name> -p src/Infrastructure -s src/Api -o Persistence/Migrations
```

## Planned structure
```
src/
  Domain/           entities, domain events
  Application/      use cases, validation, abstractions
  Infrastructure/   EF Core, Redis, LLM clients, storage
  Api/              Minimal API endpoints, middleware
web/                React + TypeScript (Vite)
tests/              unit, integration (Testcontainers), architecture
docker-compose.yml  Postgres (pgvector) + Redis
```
