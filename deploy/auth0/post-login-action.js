/**
 * Auth0 Action (trigger: Login / Post Login). Adds the claims the API needs to every access token.
 *
 * The API reads them under the namespaced names configured in render.yaml:
 *   Auth__RolesClaim  = https://kb-assistant.example/roles
 *   Auth__TenantClaim = https://kb-assistant.example/tenant_id
 *   Auth__NameClaim   = https://kb-assistant.example/name
 * (Auth0 only accepts custom claims under a namespace you own; the URL need not resolve.)
 *
 * Each user's role and tenant live in Auth0 under User Management > Users > <user> > app_metadata, for example:
 *   { "roles": ["admin"], "tenant_id": "a0000000-0000-0000-0000-000000000001" }
 * Users in the same tenant_id share workspaces; a different tenant_id is a separate, invisible organisation.
 */
exports.onExecutePostLogin = async (event, api) => {
  const ns = "https://kb-assistant.example/";
  const meta = event.user.app_metadata || {};

  const allowed = ["admin", "editor", "viewer"];
  const roles = (Array.isArray(meta.roles) ? meta.roles : []).filter((r) => allowed.includes(r));

  // Least privilege: a user with no (valid) role is a viewer. A user with no tenant_id gets no tenant claim, and the
  // API then refuses every request (it fails closed rather than guessing a tenant).
  api.accessToken.setCustomClaim(ns + "roles", roles.length > 0 ? roles : ["viewer"]);
  if (typeof meta.tenant_id === "string" && meta.tenant_id.length > 0) {
    api.accessToken.setCustomClaim(ns + "tenant_id", meta.tenant_id);
  }
  api.accessToken.setCustomClaim(ns + "name", event.user.name || event.user.email || event.user.user_id);
};
