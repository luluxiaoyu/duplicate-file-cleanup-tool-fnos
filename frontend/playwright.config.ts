import { defineConfig } from '@playwright/test'
export default defineConfig({
  testDir: './e2e',
  use: { baseURL: 'http://127.0.0.1:4173/app/duplicate-file-cleanup-tool-fnos/', channel: 'chrome', headless: true, screenshot: 'only-on-failure' },
  webServer: { command: 'npm run dev -- --port 4173 --strictPort', url: 'http://127.0.0.1:4173/app/duplicate-file-cleanup-tool-fnos/', reuseExistingServer: !process.env.CI },
  reporter: 'list',
})
