# Free deployment (Render + Neon + Auth0)

Total cost: **$0**. Everything below is a free tier; read "What free costs you" before relying on it.

```
Browser ──> kb-vb26-web   Render static site (free, always on)
   │            │ calls (CORS)
   │            v
   │        kb-vb26-api    Render free web service (Docker) ──> kb-vb26-redis (Render free Key Value)
   │                                   └──────────────────────> Neon Postgres + pgvector (free)
   └──────── sign-in ──────> Auth0 (free) ; the API validates its tokens
```

The configuration is `render.yaml`. For a self-contained paid variant that runs its own Keycloak (about $45/month), see [DEPLOY-RENDER.md](DEPLOY-RENDER.md).

## What free costs you
- **Cold starts.** The API sleeps after 15 minutes without traffic; the next request takes about a minute while it wakes and the app starts. Live ingestion updates (SignalR) drop while it sleeps and reconnect after.
- **Uploaded files are not kept.** Free web services have no disk, so original files vanish on every restart or spin-down. Their searchable chunks and embeddings stay in Postgres, so questions still work; "reprocess" a document needs a re-upload.
- **Shared hours.** Free web services draw from one 750-hour monthly pool per Render workspace. Your workspace already has several free web services; this adds one. The static site uses no hours. If the pool runs out, Render suspends free services until the month resets.
- **Neon free Postgres** has about 0.5 GB of storage and suspends its compute when idle (a second or so to resume).
- **Render free Key Value** is 25 MB and not guaranteed durable. It only holds rate-limit counters, idempotency records, locks and the semantic cache, all of which can be rebuilt.
- **Answers use the offline stand-in** (quotes from your documents, no real language model) unless you add an Anthropic key, which is not free.
- **Auth0 free** allows 25,000 monthly active users and has no production SLA.

## 1. Neon (database)
1. Sign up at neon.tech, create a project (any region near Oregon, e.g. AWS us-west-2).
2. Open **Connect**, turn **Connection pooling off**, and copy the connection string. It must look like `postgresql://USER:PASSWORD@ep-xxxx.REGION.aws.neon.tech/neondb?sslmode=require` with **no `-pooler`** in the host (the pooler's transaction mode breaks prepared statements). Keep it private; you will paste it into Render yourself.
3. Nothing else: the first API start runs the migrations, which also enable the `vector` extension.

## 2. Auth0 (sign-in)
1. Sign up at auth0.com and create a tenant. Note its domain, e.g. `dev-abc123.us.auth0.com`. The issuer is `https://dev-abc123.us.auth0.com/` **with the trailing slash**.
2. **Applications → Create Application** → type **Single Page Web Applications**, name "Knowledge Assistant". Note the **Client ID**. After step 3 below you will know the web URL; set on this application:
   - Allowed Callback URLs: `https://kb-vb26-web.onrender.com/`
   - Allowed Logout URLs: `https://kb-vb26-web.onrender.com/`
   - Allowed Web Origins: `https://kb-vb26-web.onrender.com`
   - Under Advanced → Grant Types make sure **Authorization Code** and **Refresh Token** are on, and in Settings turn on **Refresh Token Rotation**.
3. **Applications → APIs → Create API**: Identifier `kb-api`, algorithm RS256. In its Settings turn on **Allow Offline Access**.
4. **Actions → Library → Build Custom** → trigger **Login / Post Login**, name "Add API claims", paste `deploy/auth0/post-login-action.js`, **Deploy**. Then **Actions → Flows → Login**, drag the action between Start and Complete, **Apply**.
5. **Authentication → Database → Username-Password-Authentication → Settings**: turn on **Disable Sign Ups**, so only users you create can sign in. Under the application's **Connections** tab, make sure this connection is enabled.
6. **User Management → Users → Create User** (email + password). Open each user and set **app_metadata**:
   ```json
   { "roles": ["admin"], "tenant_id": "a0000000-0000-0000-0000-000000000001" }
   ```
   Roles are `admin`, `editor` or `viewer`. Users sharing a `tenant_id` share data; a different `tenant_id` is a separate organisation. A user without a `tenant_id` is refused by the API.

## 3. Render (hosting)
1. Render Dashboard → **New → Blueprint** → select this repository (the branch with `render.yaml`; merge the PR chain first, or choose `claude/changes`).
2. Render lists the three resources with plan **Free**. Confirm each says free before continuing.
3. Fill the prompted values:

   | Value | What to enter |
   |---|---|
   | `Database__ConnectionString` | the Neon direct connection string |
   | `Auth__Authority` | `https://YOUR-TENANT.REGION.auth0.com/` (trailing slash) |
   | `Cors__AllowedOrigins__0` | `https://kb-vb26-web.onrender.com` |
   | `Llm__Anthropic__ApiKey` | leave empty |
   | `VITE_API_URL` | `https://kb-vb26-api.onrender.com` |
   | `VITE_OIDC_AUTHORITY` | same Auth0 issuer, trailing slash |
   | `VITE_OIDC_CLIENT_ID` | the Auth0 application's Client ID |

4. Wait for the first deploy (the API image build takes several minutes). If Render gave a service a different URL than the names above (it adds a suffix when a name is taken), correct `Cors__AllowedOrigins__0`, `VITE_API_URL` and the Auth0 URLs in step 2, then redeploy both services. A wrong origin shows as a CORS error in the browser console; a wrong issuer shows as 401.

## 4. Check it
```bash
curl https://kb-vb26-api.onrender.com/health/ready          # Healthy (allow a minute if it was asleep)
```
Open the web URL, sign in as the user you created, create a workspace, upload a short Markdown file, wait for "Ready", and ask a question. The browser suite can drive the same flow against it, but it fills the Keycloak sign-in form, so it does not apply to Auth0; use the manual check above.

## Rollback
Render Dashboard → the service → **Events** → **Rollback** (both services if both changed). Migrations are expand/contract ([ADR 0005](adr/0005-migrations-as-a-release-step.md)), so the previous API works against the newer schema. Neon keeps point-in-time history on a short window on the free plan; check its retention before depending on it.

## What was and was not verified
Verified locally: the production-built web app on one origin calling the API on another (CORS, streamed answers, uploads, live ingestion updates, role and tenant isolation) with the browser suite, 4 of 4 on two runs; a token shaped like Auth0's (namespaced role and tenant claims) is authorised and tenant-scoped through the real JWT pipeline, and without the mapping it is refused (automated tests); `postgresql://…?sslmode=require` and `redis://` connection URIs are converted correctly (unit tests).

**Not verified, because it needs your accounts:** Render accepting `render.yaml`; the Auth0 flow end to end (callback and logout URLs, refresh-token rotation, the Action's claims, the `offline_access` scope); the API connecting to Neon's direct endpoint with TLS; the free API image fitting the free build and 512 MB memory limit; cold-start timing.
