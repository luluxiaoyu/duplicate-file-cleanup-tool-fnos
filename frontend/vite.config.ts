import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath } from 'node:url'

export default defineConfig({
  base: '/app/duplicate-file-cleanup-tool-fnos/',
  plugins: [vue(), tailwindcss()],
  build: { rollupOptions: { input: {
    app: fileURLToPath(new URL('index.html', import.meta.url)),
    callback: fileURLToPath(new URL('callback.html', import.meta.url)),
  } } },
})
