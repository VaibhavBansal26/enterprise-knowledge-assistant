# Enterprise Knowledge Assistant

A production-grade RAG application: employees ask questions over internal documents and get streamed, cited answers that respect per-user permissions.

**Stack:** .NET 9 (Minimal APIs, EF Core) · React + TypeScript (Vite) · PostgreSQL + pgvector · Redis · LLM provider behind an interface

## Status
Planning. See [ROADMAP.md](ROADMAP.md).

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
