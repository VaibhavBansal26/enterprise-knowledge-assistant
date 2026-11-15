#!/bin/sh
# Writes /tmp/config.js from environment variables so one image can run anywhere (the SPA reads it at load time).
#   OIDC_AUTHORITY       full issuer URL, e.g. https://id.example.com/realms/kb
#   OIDC_AUTHORITY_PATH  path on this site's own origin, e.g. /auth/realms/kb (used when Keycloak is proxied here)
#   OIDC_CLIENT_ID       defaults to kb-web
#   OIDC_AUDIENCE        API identifier to request (Auth0), e.g. kb-api
#   OIDC_SCOPE           defaults to "openid profile email"; add offline_access for refresh tokens (Auth0)
#   API_URL              origin of the API when it is not served from this site (default: same origin)
#   OIDC_ROLES_CLAIM     name of the role claim in the access token, default "roles"
set -eu
clean() { printf '%s' "$1" | tr -cd 'A-Za-z0-9/_.:@%?=&#~+, -'; }
{
  printf 'window.__KB_CONFIG__ = {\n'
  printf '  authority: "%s",\n' "$(clean "${OIDC_AUTHORITY:-}")"
  printf '  authorityPath: "%s",\n' "$(clean "${OIDC_AUTHORITY_PATH:-}")"
  printf '  clientId: "%s",\n' "$(clean "${OIDC_CLIENT_ID:-}")"
  printf '  audience: "%s",\n' "$(clean "${OIDC_AUDIENCE:-}")"
  printf '  scope: "%s",\n' "$(clean "${OIDC_SCOPE:-}")"
  printf '  rolesClaim: "%s",\n' "$(clean "${OIDC_ROLES_CLAIM:-}")"
  printf '  apiUrl: "%s"\n' "$(clean "${API_URL:-}")"
  printf '};\n'
} > /tmp/config.js
