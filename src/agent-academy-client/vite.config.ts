/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  build: {
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [
            { name: 'vendor-react', test: /node_modules[\\/]react(-dom)?[\\/]/, priority: 20 },
            { name: 'vendor-fluent', test: /node_modules[\\/]@fluentui/, priority: 15 },
            { name: 'vendor-markdown', test: /node_modules[\\/](react-markdown|remark-gfm|unified|remark-parse|mdast|micromark|decode-named-character-reference|character-entities)/, priority: 15 },
            { name: 'vendor-signalr', test: /node_modules[\\/]@microsoft[\\/]signalr/, priority: 15 },
            { name: 'vendor', test: /node_modules/, priority: 5 },
          ],
        },
      },
    },
  },
  test: {
    exclude: ['e2e/**', 'node_modules/**'],
    coverage: {
      provider: 'v8',
      reporter: ['text', 'cobertura', 'html'],
      reportsDirectory: './coverage',
      include: ['src/**/*.{ts,tsx}'],
      exclude: [
        'src/**/*.test.{ts,tsx}',
        'src/**/*.spec.{ts,tsx}',
        'src/**/test-utils/**',
        'src/main.tsx',
        'src/vite-env.d.ts',
      ],
    },
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
