import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The portal (Portal/WreckfestPortal) serves the built app under each server's path;
// MSBuild copies dist/ to its app folder. In development, Vite serves the app and forwards
// the API and the hub to a controller running on this machine, so no CORS setup is needed.
const controller = 'http://127.0.0.1:5100'

export default defineConfig({
  // Relative asset links, so one build works under any server's path: index.html's <base>
  // says where they resolve.
  base: './',
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
    // prePaint.test.ts reads styles.css as text; otherwise Vitest stubs every CSS import.
    css: { include: [/styles\.css/] },
  },
})
