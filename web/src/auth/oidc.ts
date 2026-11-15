import { InMemoryWebStorage, UserManager, WebStorageStateStore } from "oidc-client-ts";

// Resolution order: runtime config (container) > build-time Vite variable > local development default.
const runtime = window.__KB_CONFIG__ ?? {};
const authority =
  runtime.authority ||
  (runtime.authorityPath ? `${window.location.origin}${runtime.authorityPath}` : undefined) ||
  import.meta.env.VITE_OIDC_AUTHORITY ||
  "http://localhost:8081/realms/kb";
const clientId = runtime.clientId || import.meta.env.VITE_OIDC_CLIENT_ID || "kb-web";
// Auth0 and Entra ID need the API's identifier (audience) on the sign-in request, and only issue refresh tokens for offline_access.
const audience = runtime.audience || import.meta.env.VITE_OIDC_AUDIENCE || undefined;
const scope = runtime.scope || import.meta.env.VITE_OIDC_SCOPE || "openid profile email";
// Identity providers that only allow namespaced custom claims (Auth0) name the role claim e.g. "https://kb.example/roles".
const rolesClaim = runtime.rolesClaim || import.meta.env.VITE_OIDC_ROLES_CLAIM || "roles";

/**
 * Authorization Code flow with PKCE against the identity provider (Keycloak).
 *
 * Tokens are held in memory only, never in localStorage/sessionStorage, so an XSS bug cannot read them
 * from storage. A page reload therefore starts a quick silent round-trip to the identity provider, whose
 * session cookie signs the user straight back in. Only the short-lived login `state` (needed to survive
 * the redirect) lives in sessionStorage. Access tokens are renewed in the background via the refresh token.
 */
export const userManager = new UserManager({
  authority,
  client_id: clientId,
  redirect_uri: `${window.location.origin}/`,
  post_logout_redirect_uri: `${window.location.origin}/`,
  response_type: "code",
  scope,
  extraQueryParams: audience ? { audience } : undefined,
  automaticSilentRenew: true,
  userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
  stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
});

/** Reads the roles claim (`roles`, or the configured namespaced name) from an access token. Display only: the API enforces authorization. */
export function rolesFromAccessToken(accessToken: string): string[] {
  try {
    const payload = accessToken.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    const claims = JSON.parse(atob(payload)) as Record<string, unknown>;
    const roles = claims[rolesClaim];
    if (typeof roles === "string") return [roles];
    return Array.isArray(roles) ? roles.filter((r): r is string => typeof r === "string") : [];
  } catch {
    return [];
  }
}
