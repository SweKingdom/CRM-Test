import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// The SPA is same-origin with the API (no CORS): the dev server forwards the API, the sign-in endpoints and the
// OIDC callbacks. The Host header is kept (no changeOrigin) so the sign-in redirect URI and the CSRF origin check
// see http://localhost:3000, which the Keycloak client allows.
const api = "http://localhost:8080";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
    strictPort: true,
    proxy: {
      "/api": api,
      "/auth": api,
      "^/signin-oidc-.*": api,
    },
  },
  // Unit tests only; the Playwright specs in e2e/ run with `npm run e2e`.
  test: { include: ["src/**/*.test.ts"] },
});
