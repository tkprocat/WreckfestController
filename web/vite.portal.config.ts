import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// The portal's page (Portal/WreckfestPortal): a second entry of this app, so it shares the
// theme and components. MSBuild copies dist-portal/ to the portal's wwwroot (and dist/,
// the controllers' web app, to its app folder). In
// development, Vite forwards /api to a portal running on this machine.
const portal = 'http://127.0.0.1:5000'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    open: '/portal.html',
    proxy: { '/api': portal },
  },
  build: {
    outDir: 'dist-portal',
    emptyOutDir: true,
    rollupOptions: { input: fileURLToPath(new URL('./portal.html', import.meta.url)) },
  },
})
