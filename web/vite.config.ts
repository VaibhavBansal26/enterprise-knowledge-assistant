import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": "http://localhost:8080",
      "/health": "http://localhost:8080",
      // SignalR: negotiate over HTTP, then upgrade to a WebSocket.
      "/hubs": { target: "http://localhost:8080", ws: true },
    },
  },
});
