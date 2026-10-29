import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    globals: true,
    environment: "jsdom",
    environmentOptions: {
      jsdom: {
        resources: "usable",
      },
    },
    setupFiles: ["src/test-setup.ts"],
    testTimeout: 10000,
  },
});
