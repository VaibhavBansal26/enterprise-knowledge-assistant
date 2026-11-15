# Static SPA served by nginx, which also reverse-proxies the API so the browser sees one origin (no CORS).
FROM node:22-alpine AS build
WORKDIR /web
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# OIDC settings are baked in at build time (Vite). Override with --build-arg for a real deployment.
ARG VITE_OIDC_AUTHORITY=http://localhost:8081/realms/kb
ARG VITE_OIDC_CLIENT_ID=kb-web
ENV VITE_OIDC_AUTHORITY=$VITE_OIDC_AUTHORITY VITE_OIDC_CLIENT_ID=$VITE_OIDC_CLIENT_ID
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /web/dist /usr/share/nginx/html
# DNS_RESOLVER: Docker's embedded DNS by default; use the cluster/platform resolver elsewhere (e.g. kube-dns).
ENV API_UPSTREAM=http://api:8080 DNS_RESOLVER=127.0.0.11
EXPOSE 8080
