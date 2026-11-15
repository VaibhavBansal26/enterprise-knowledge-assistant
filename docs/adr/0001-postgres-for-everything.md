# 0001. One PostgreSQL for data, vectors, full-text search and the job queue
**Status:** accepted

**Context.** The system needs relational data, vector similarity, keyword search and a durable background queue.

**Decision.** Use PostgreSQL 17 with pgvector (HNSW index), built-in full-text search, and an outbox table consumed with `FOR UPDATE SKIP LOCKED`. Redis is used only for ephemeral state (rate limits, idempotency, locks, semantic cache, SignalR backplane), never as the source of truth.

**Consequences.** Fewer moving parts and transactional enqueue (a document row and its job commit together). Vector search will not match a dedicated engine at very large scale; the retrieval code sits behind one query, so moving it later is contained. The semantic cache is a brute-force scan and would need an index at higher volume.
