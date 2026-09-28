import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The controller serves the built app from wwwroot next to its exe; MSBuild copies
// dist/ there. In development, Vite serves the app and forwards the API and the hub to
// a controller running on this machine, so no CORS setup is needed.
const controller = 'http://127.0.0.1:5100'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    proxy: {
      '/api': controller,
      '/hubs': { target: controller, ws: true },
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  test: {
    environment: 'jsdom',
  },
})
