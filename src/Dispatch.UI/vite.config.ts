import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Dev server proxies API + SignalR to the running Dispatch service (port 8080).
// `npm run build` emits to dist/, which Dispatch.Web embeds and serves at runtime.
export default defineConfig({
  plugins: [react()],
  build: { outDir: "dist", emptyOutDir: true },
  server: {
    proxy: {
      "/api": "http://localhost:8420",
      "/health": "http://localhost:8420",
      "/hub": { target: "http://localhost:8420", ws: true },
    },
  },
});
