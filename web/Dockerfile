# Static SPA served by nginx, which also reverse-proxies the API so the browser sees one origin (no CORS).
FROM node:22-alpine AS build
WORKDIR /web
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# OIDC settings can be baked in here, but prefer the runtime variables below so one image runs anywhere.
ARG VITE_OIDC_AUTHORITY=http://localhost:8081/realms/kb
ARG VITE_OIDC_CLIENT_ID=kb-web
ENV VITE_OIDC_AUTHORITY=$VITE_OIDC_AUTHORITY VITE_OIDC_CLIENT_ID=$VITE_OIDC_CLIENT_ID
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --chmod=755 10-dns-resolver.envsh /docker-entrypoint.d/10-dns-resolver.envsh
COPY --chmod=755 40-runtime-config.sh /docker-entrypoint.d/40-runtime-config.sh
COPY --from=build /web/dist /usr/share/nginx/html
# DNS_RESOLVER is detected from /etc/resolv.conf at start (see 10-dns-resolver.envsh); set it to override.
# KEYCLOAK_UPSTREAM: where /auth/ is proxied (only used when Keycloak is served under this site's origin).
ENV API_UPSTREAM=http://api:8080 KEYCLOAK_UPSTREAM=http://keycloak:8080
EXPOSE 8080
