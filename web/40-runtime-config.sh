#!/bin/sh
# Writes /tmp/config.js from environment variables so one image can run anywhere (the SPA reads it at load time).
#   OIDC_AUTHORITY       full issuer URL, e.g. https://id.example.com/realms/kb
#   OIDC_AUTHORITY_PATH  path on this site's own origin, e.g. /auth/realms/kb (used when Keycloak is proxied here)
#   OIDC_CLIENT_ID       defaults to kb-web
set -eu
clean() { printf '%s' "$1" | tr -cd 'A-Za-z0-9/_.:@%?=&#~+,-'; }
{
  printf 'window.__KB_CONFIG__ = {\n'
  printf '  authority: "%s",\n' "$(clean "${OIDC_AUTHORITY:-}")"
  printf '  authorityPath: "%s",\n' "$(clean "${OIDC_AUTHORITY_PATH:-}")"
  printf '  clientId: "%s"\n' "$(clean "${OIDC_CLIENT_ID:-}")"
  printf '};\n'
} > /tmp/config.js
