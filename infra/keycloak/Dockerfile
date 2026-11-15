# Keycloak for hosted deployments (Render). Same realm as local development, minus the dev-only pieces:
#   - the password-grant test client (kb-dev-cli) is removed,
#   - user passwords come from environment variables instead of the checked-in sample values,
#   - the web client accepts relative redirect URIs, so no public URL has to be known at build time.
# Runs in start-dev mode (embedded H2 database): users and sessions reset when the container is replaced, which suits
# a demo. For a real deployment use a managed identity provider and point Auth__Authority at it (see docs/RUNBOOK.md).
FROM alpine:3.20 AS realm
RUN apk add --no-cache jq
COPY kb-realm.json render-realm.jq /
RUN jq -f /render-realm.jq /kb-realm.json > /kb-realm.render.json

FROM quay.io/keycloak/keycloak:26.0
COPY --from=realm --chown=1000:0 /kb-realm.render.json /opt/keycloak/data/import/kb-realm.json
COPY --chmod=755 entrypoint.sh /entrypoint.sh
# Served under /auth so the web container can proxy it on the site's own origin (one URL for browser, API and IdP).
ENV KC_HTTP_ENABLED=true \
    KC_HTTP_RELATIVE_PATH=/auth \
    KC_HOSTNAME_STRICT=false \
    KC_PROXY_HEADERS=xforwarded
ENTRYPOINT ["/entrypoint.sh"]
