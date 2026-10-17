import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { onUnauthorized } from './api/client'
import { startHub } from './realtime/hub'
import { router } from './router'
import { useAuthStore } from './stores/auth'
import './styles.css'

const app = createApp(App).use(createPinia()).use(router)

// The portal stopped vouching for the user here. Ask it why: a session that ended is
// forgotten (load regroups the hub, so it stops getting admin messages) and the user asked
// to sign in again, coming back to where they were; access to this server that was taken
// away sends them to say so. While the portal cannot answer, nothing changes.
onUnauthorized(async () => {
  const auth = useAuthStore()
  const current = router.currentRoute.value
  if (!(await auth.load())) {
    return
  }

  if (!auth.authenticated) {
    if (current.name !== 'login') {
      void router.push({ name: 'login', query: { redirect: current.fullPath } })
    }
  } else if (current.meta.requiresServer && !auth.managesThisServer) {
    void router.push({ name: 'no-access', query: { from: current.fullPath } })
  }
})

app.mount('#app')

// Live updates for everyone; the page works without them if the hub is unreachable.
startHub().catch(() => undefined)
