# 0005. Migrations run as a separate release step
**Status:** accepted

**Context.** Applying migrations on app startup races between replicas, runs with the app's privileges, and lets a failing migration take the new version down after traffic has moved.

**Decision.** Build a self-contained EF migrations bundle (`--target migrator` in `src/Api/Dockerfile`). The release runs it to completion before the new API starts; `Database__MigrateOnStartup` stays true only in local development. Migrations follow expand/contract: add nullable columns and tables first, remove or tighten only in a later release.

**Consequences.** A bad migration blocks the release with the old version still serving. Rolling back code is safe because the previous version works against the expanded schema; rolling back data means restoring a backup (see the runbook).
