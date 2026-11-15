/**
 * Deployment settings. Resolution order: runtime config (written by the web container at start-up, see
 * 40-runtime-config.sh) > build-time Vite variable (static hosting) > development default.
 */
const runtime = window.__KB_CONFIG__ ?? {};

/** Origin of the API, without a trailing slash. Empty means "same origin" (dev proxy, or nginx in front). */
export const apiBase = (runtime.apiUrl || import.meta.env.VITE_API_URL || "").replace(/\/+$/, "");
