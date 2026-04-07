/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    exclude: ['e2e/**', 'node_modules/**'],
  },
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5066',
        changeOrigin: true,
      },
      '/healthz': {
        target: 'http://localhost:5066',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5066',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
