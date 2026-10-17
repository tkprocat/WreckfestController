import { fileURLToPath, URL } from 'node:url'
import { defineConfig, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'

// The portal (Portal/WreckfestPortal) serves the built app under each server's path;
// MSBuild copies dist/ to its app folder. In development, Vite serves the app as one of the
// portal's servers (WFC_DEV_SERVER, default vanilla: open http://localhost:5173/vanilla/)
// and forwards the portal's API and that server's API and hub to a portal running on this
// machine, which signs people in and vouches for them to the controller. No CORS setup.
const portal = 'http://127.0.0.1:5000'
const devServer = process.env.WFC_DEV_SERVER ?? 'vanilla'

/** In development, what the portal does in production: writes the server's path into <base>. */
function devBase(): Plugin {
  return {
    name: 'wfc-dev-base',
    apply: 'serve',
    transformIndexHtml: (html) => html.replace('<base href="/" />', `<base href="/${devServer}/" />`),
  }
}

export default defineConfig({
  // Relative asset links, so one build works under any server's path: index.html's <base>
  // says where they resolve.
  base: './',
  plugins: [vue(), devBase()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    proxy: {
      // The portal's own API at the root: signing in, the accounts, the server list.
      '^/api/': portal,
      // The server's API and hub, under its path.
      [`^/${devServer}/(api|hubs|openapi)(/|$)`]: { target: portal, ws: true },
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
