#!/bin/bash
# Injects the sample users' passwords into the realm file, then starts Keycloak.
set -euo pipefail
realm=/opt/keycloak/data/import/kb-realm.json

for user in ALICE BOB CAROL DAVE; do
  var="KB_PASSWORD_${user}"
  value="${!var:-}"
  if [ -z "$value" ]; then
    echo "entrypoint: ${var} must be set (password for the sample user ${user,,})" >&2
    exit 1
  fi
  # '#' is not in the base64 alphabet that generated secrets use, so it is a safe sed delimiter.
  sed -i "s#__PW_${user}__#${value}#g" "$realm"
done

# Render tells the service which port to listen on through PORT.
export KC_HTTP_PORT="${PORT:-8080}"
exec /opt/keycloak/bin/kc.sh start-dev --import-realm
