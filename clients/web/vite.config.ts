import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
import { fileURLToPath, URL } from "node:url";

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  server: {
    port: 5173,
    proxy: {
      "/snapshots": {
        target: "ws://127.0.0.1:5088",
        ws: true,
      },
      "/play": {
        target: "ws://127.0.0.1:5088",
        ws: true,
      },
      // The level document the client fetches next to the socket; an absolute socket URL reaches
      // the server directly (it answers with CORS), a relative one shares this origin.
      "/level": {
        target: "http://127.0.0.1:5088",
      },
    },
  },
});
