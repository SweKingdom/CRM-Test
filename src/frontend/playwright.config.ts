import { defineConfig } from "@playwright/test";

// The mocked specs need only the Vite dev server (the API calls are intercepted); the sign-in smoke test needs the whole stack.
export default defineConfig({
  testDir: "e2e",
  use: { baseURL: "http://localhost:3000" },
  webServer: { command: "npm run dev", url: "http://localhost:3000", reuseExistingServer: true },
});
