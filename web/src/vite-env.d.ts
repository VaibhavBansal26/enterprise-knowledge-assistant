/// <reference types="vite/client" />

interface Window {
  /** Runtime settings written by the container at start-up; empty in development. */
  __KB_CONFIG__?: { authority?: string; authorityPath?: string; clientId?: string };
}
