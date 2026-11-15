# 0002. Hybrid retrieval with permissions enforced inside the SQL
**Status:** accepted

**Context.** Vector search misses exact terms (names, codes); keyword search misses paraphrase. Filtering results after retrieval can leak content and starves top-K.

**Decision.** Run vector and full-text arms in one query, fuse with Reciprocal Rank Fusion, and apply tenant, workspace and soft-delete filters inside both arms. EF global query filters fail closed when no tenant is resolved.

**Consequences.** No code path retrieves another tenant's chunks, so a prompt cannot expose them. The cost is a more complex query that is covered by integration tests against real Postgres.
