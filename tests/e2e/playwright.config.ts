import { defineConfig } from '@playwright/test'

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  workers: 1,
  timeout: 600_000,
  expect: { timeout: 15_000 },
  reporter: 'list',
  // The pilot journey first: it creates the knowledge base, source, run and assistant whose pages the accessibility
  // spec scans. On a fresh stack (CI) the accessibility spec would otherwise find no detail page to scan.
  // The setup wizard needs a stack of its own (scripts/Start-E2E.ps1 -Setup), so it runs alone: --project=setup on that stack,
  // --project=pilot --project=accessibility on the normal one (its spec skips while setup is not open).
  projects: [
    { name: 'pilot', testMatch: /pilot\.spec\.ts/ },
    { name: 'accessibility', testMatch: /accessibility\.spec\.ts/, dependencies: ['pilot'] },
    {
      name: 'setup',
      testMatch: /setup\.spec\.ts/,
      outputDir: 'test-results/setup',
      // It types client secrets, a password and the setup code: never a trace, screenshot or video, whatever the defaults become.
      use: { trace: 'off', screenshot: 'off', video: 'off' },
    },
  ],
  use: {
    baseURL: process.env.ADMIN_UI_URL ?? 'https://localhost:5174',
    browserName: 'chromium',
    viewport: { width: 1280, height: 900 },
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
    ignoreHTTPSErrors: true,
    // The admin UI marks elements with data-test (src/admin-ui).
    testIdAttribute: 'data-test',
    // Artifacts could capture show-once keys or credentials.
    trace: 'off',
    screenshot: 'off',
    video: 'off',
  },
})
