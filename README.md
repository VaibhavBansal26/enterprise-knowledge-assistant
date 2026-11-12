# Enterprise Knowledge Assistant

A production-grade RAG application: employees ask questions over internal documents and get streamed, cited answers that respect per-user permissions.

**Stack:** .NET 9 (Minimal APIs, EF Core) · React + TypeScript (Vite) · PostgreSQL + pgvector · Redis · LLM provider behind an interface

## Status
Phase 0 (foundations) scaffolded. See [ROADMAP.md](ROADMAP.md).

## Quick start
```bash
# Postgres (pgvector) + Redis
docker compose up -d

# API (needs .NET 9 SDK) -> http://localhost:5000 (see launch output)
dotnet run --project src/Api

# Web -> http://localhost:5173
cd web && npm install && npm run dev
```
Run everything in containers: `docker compose --profile full up --build` (API on :8080).

Health endpoints: `/health/live` (process up), `/health/ready` (Postgres + Redis reachable).

Tests: `dotnet test`

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
