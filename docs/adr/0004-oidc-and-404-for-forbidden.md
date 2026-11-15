# 0004. External OIDC provider; 404 instead of 403 across tenants
**Status:** accepted

**Context.** Authentication should not be re-implemented, and responses must not reveal that another tenant's resource exists.

**Decision.** Validate JWTs from any OIDC provider (Keycloak in the sample stack; the browser uses authorization code with PKCE). Roles (admin, editor, viewer) come from token claims; the tenant comes from a claim. Resources outside the caller's tenant or workspace membership return 404.

**Consequences.** Swapping the identity provider is configuration. The bundled Keycloak runs in dev mode with sample users and is not for production; use a managed provider and remove the `kb-dev-cli` password-grant client.

**Addendum (free deployment).** Role, tenant and display-name claim names are configurable (`Auth__RolesClaim`, `Auth__TenantClaim`, `Auth__NameClaim`), because providers such as Auth0 only allow namespaced custom claims. A claims transformation copies them to the canonical names, so nothing else in the application knows which provider is in use.
