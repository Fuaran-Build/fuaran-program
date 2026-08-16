import { defineConfig } from "vite";

// Port from ports.json — the workspace band map allocates this sample's slot.
export default defineConfig({
  server: { port: 24070, strictPort: true },
  preview: { port: 24070, strictPort: true },
});
