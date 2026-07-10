import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Dev server proxies API + SignalR to the running Dispatch service on the dashboard port (8420).
// The dashboard is HTTPS-only (self-signed in dev), so target https with secure:false.
// `npm run build` emits to dist/, which Dispatch.Web embeds and serves at runtime.
const target = "https://localhost:8420";
export default defineConfig({
  plugins: [react()],
  build: { outDir: "dist", emptyOutDir: true },
  server: {
    proxy: {
      "/api": { target, secure: false },
      "/health": { target, secure: false },
      "/hub": { target, ws: true, secure: false },
    },
  },
});
