import { InMemoryWebStorage, UserManager, WebStorageStateStore } from "oidc-client-ts";

const authority = import.meta.env.VITE_OIDC_AUTHORITY ?? "http://localhost:8081/realms/kb";
const clientId = import.meta.env.VITE_OIDC_CLIENT_ID ?? "kb-web";

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
  scope: "openid profile email",
  automaticSilentRenew: true,
  userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
  stateStore: new WebStorageStateStore({ store: window.sessionStorage }),
});

/** Reads the `roles` claim from an access token. Display only: the API enforces authorization. */
export function rolesFromAccessToken(accessToken: string): string[] {
  try {
    const payload = accessToken.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    const claims = JSON.parse(atob(payload)) as { roles?: unknown };
    return Array.isArray(claims.roles) ? claims.roles.filter((r): r is string => typeof r === "string") : [];
  } catch {
    return [];
  }
}
