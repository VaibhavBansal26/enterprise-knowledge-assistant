# Deploying to Render with Keycloak (paid, self-contained)

> The **free** setup (Render free tier + Neon + Auth0) is in [DEPLOY-FREE.md](DEPLOY-FREE.md) and `render.yaml`. This page is the paid variant that also hosts Keycloak, defined in `deploy/render.keycloak.yaml` (choose that path when creating the Blueprint).

`deploy/render.keycloak.yaml` describes the whole stack. Run it locally first with `deploy/docker-compose.render-like.yml`, which mirrors the same topology (this passed the Playwright suite, see below).

```
Internet ──> kb-web (nginx, public)  ──/api, /hubs, /health──> kb-api (public, health-checked)
                  │                                              ├──> kb-db      (Render Postgres, pgvector)
                  └──/auth──> kb-keycloak (private)              └──> kb-redis   (Render Key Value)
```

Everything the browser talks to is on one origin (`kb-web`), so there is no CORS and the token issuer is a single URL.

## Cost
Approximate list prices at the time of writing, **check the Render dashboard before confirming**:

| Resource | Plan | About |
|---|---|---|
| kb-db (Postgres 17) | basic-256mb | $6 / month |
| kb-redis (Key Value) | free | $0 |
| kb-keycloak (private service) | standard (2 GB) | $25 / month |
| kb-api + 1 GB disk | starter | $7 + $0.25 / month |
| kb-web | starter | $7 / month |
| **Total** | | **about $45 / month** |

Keycloak is the expensive part. If you have an OIDC provider already (Auth0, Entra ID, Okta, a managed Keycloak), delete `kb-keycloak` from the blueprint, set `Auth__Authority` (and remove `Auth__MetadataAddress`) to that issuer, and set `OIDC_AUTHORITY` plus `OIDC_CLIENT_ID` on `kb-web`; the total drops to about $20.

## Steps
1. **Merge the PR chain** (#6, #7, #8, #9) into `main`, or point the Blueprint at branch `claude/changes`.
2. Render Dashboard → **New → Blueprint** → select this repository. Render validates the blueprint file and shows the services and prices. Review them, then continue.
3. When asked for values:
   - `Auth__Authority`: `https://kb-web.onrender.com/auth/realms/kb`. This is a guess; fix it in step 5.
   - `Llm__Anthropic__ApiKey`: leave empty to keep the offline stand-in, or paste a key (and set `Llm__Provider=anthropic` on `kb-api`).
4. Wait for all five resources to go live. First deploy takes several minutes (Keycloak imports its realm; the API applies migrations on start).
5. **Check the public URL.** Open `kb-web` in the dashboard. If its URL is not exactly `https://kb-web.onrender.com` (Render adds a suffix when a name is taken), edit `Auth__Authority` on `kb-api` to `<real URL>/auth/realms/kb` and let it redeploy. Until these match, every API call returns 401.
6. **Get the sample users' passwords** from `kb-keycloak` → Environment (`KB_PASSWORD_ALICE` is the admin; BOB editor; CAROL viewer; DAVE admin of a second tenant). Sign in at the web URL. Change them, or replace the sample users, before inviting anyone.
7. **Smoke test:** `curl <web URL>/health/ready` returns `Healthy`; then run the browser suite against it:
   ```bash
   cd tests/e2e && npm ci && npx playwright install chromium
   BASE_URL=<web URL> E2E_PASSWORD_ALICE=... E2E_PASSWORD_CAROL=... E2E_PASSWORD_DAVE=... npx playwright test
   ```

## Rollback
- **Code:** Render Dashboard → the service → **Events** → **Rollback** to the previous deploy (do `kb-web` and `kb-api` together if both changed). Migrations are expand/contract ([ADR 0005](adr/0005-migrations-as-a-release-step.md)), so the previous version works against the newer schema.
- **Data:** restore from Render's Postgres backups into a new database and point `Database__ConnectionString` at it. Confirm your plan's retention and point-in-time options in the dashboard; they depend on the plan.
- Render keeps the old instance serving until the new one passes `/health/ready`, so a deploy whose migration fails leaves the previous version up.

## What was verified locally, and what was not
Verified with `deploy/docker-compose.render-like.yml` (same images, same variables, `postgres://` and `redis://` URIs, Keycloak only reachable through `/auth`, API as root on a root-owned volume): migrations on start, `/health/ready`, sign-in through `/auth` with the issuer matching `Auth__Authority`, and all four Playwright tests.

**Not verified, because it needs a real Render account:**
- Render accepting `deploy/render.keycloak.yaml` as written (field names, `keyvalue` type, `fromDatabase`/`fromService` references). Dashboard validation will say; fix what it flags.
- Render's private network names and ports (`kb-keycloak:10000`, `kb-api:8080`), and that nginx's resolver detection works there.
- The persistent disk being writable by the API (the image runs as root for this reason) and surviving redeploys.
- Keycloak running comfortably in 2 GB, and its first-start time.
- Render's edge sending `X-Forwarded-Proto: https` so Keycloak builds `https://` URLs.

## Known limits of this deployment
- Keycloak runs in `start-dev` mode on an embedded database: users and sessions reset when its container is replaced, and there is no high availability. Fine for a demo; use a managed identity provider for anything real.
- One API instance (blobs live on its disk).
- `Usage__DefaultMonthlyCostLimitUsd` is set to 5 so a leaked sample account cannot run up a large LLM bill.
