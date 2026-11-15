# Architecture decision log

| # | Decision |
|---|---|
| [0001](0001-postgres-for-everything.md) | One PostgreSQL for data, vectors, full-text search and the job queue |
| [0002](0002-hybrid-retrieval-permissions-in-sql.md) | Hybrid retrieval with permissions enforced inside the SQL |
| [0003](0003-llm-behind-an-interface.md) | LLM and embeddings behind interfaces, with offline stand-ins |
| [0004](0004-oidc-and-404-for-forbidden.md) | External OIDC provider; 404 instead of 403 across tenants |
| [0005](0005-migrations-as-a-release-step.md) | Migrations run as a separate release step |
| [0006](0006-eval-gate-in-ci.md) | Quality evals gate CI with a committed baseline |
