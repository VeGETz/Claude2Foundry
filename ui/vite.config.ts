import { defineConfig } from 'vite'
import preact from '@preact/preset-vite'

export default defineConfig({
  plugins: [preact()],
  build: {
    outDir: '../src/Claude2Foundry/wwwroot/_ui',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api/admin': 'http://127.0.0.1:8787',
      '/v1': 'http://127.0.0.1:8787',
    },
  },
})
