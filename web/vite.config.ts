import { defineConfig } from "vite";

export default defineConfig({
  build: {
    outDir: "../src/Neuropil.Server/wwwroot",
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      "/ws": { target: "ws://127.0.0.1:5080", ws: true },
      "/api": "http://127.0.0.1:5080",
    },
  },
});
