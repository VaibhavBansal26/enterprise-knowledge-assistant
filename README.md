# Enterprise Knowledge Assistant

A production-grade RAG application: employees ask questions over internal documents and get streamed, cited answers that respect per-user permissions.

**Stack:** .NET 9 (Minimal APIs, EF Core) · React + TypeScript (Vite) · PostgreSQL + pgvector · Redis · LLM provider behind an interface

## Status
Phases 0 (foundations), 1 (CRUD and data layer), 2 (middleware) and 3 (auth and multi-tenancy) done. See [ROADMAP.md](ROADMAP.md).

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
