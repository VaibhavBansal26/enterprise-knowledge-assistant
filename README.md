# Enterprise Knowledge Assistant

A production-grade RAG application: employees ask questions over internal documents and get streamed, cited answers that respect per-user permissions.

**Stack:** .NET 9 (Minimal APIs, EF Core) · React + TypeScript (Vite) · PostgreSQL + pgvector · Redis · LLM provider behind an interface

## Status
Phase 0 (foundations) and Phase 1 (CRUD and data layer) done. See [ROADMAP.md](ROADMAP.md).

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

## Tests
```bash
dotnet test   # integration tests start a throwaway Postgres via Testcontainers, so Docker must be running
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
