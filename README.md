# Enterprise Knowledge Assistant

A production-grade RAG application: employees ask questions over internal documents and get streamed, cited answers that respect per-user permissions.

**Stack:** .NET 9 (Minimal APIs, EF Core) · React + TypeScript (Vite) · PostgreSQL + pgvector · Redis · LLM provider behind an interface

## Status
Phases 0 (foundations), 1 (CRUD and data layer) and 2 (middleware) done. See [ROADMAP.md](ROADMAP.md).

## Quick start
```bash
# Postgres (pgvector) on :5433 and Redis on :6380
# (non-default host ports avoid clashing with locally installed services;
#  override with POSTGRES_PORT / REDIS_PORT)
docker compose up -d

# API on :8080 (needs .NET 9 SDK); applies EF migrations in Development
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

## Cross-cutting behaviour
Request pipeline, in order: correlation ID → exception handling → request logging → security headers → compression → CORS → rate limiting → idempotency → endpoints.

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
