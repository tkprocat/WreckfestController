import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { onUnauthorized } from './api/client'
import { startHub } from './realtime/hub'
import { router } from './router'
import { useAuthStore } from './stores/auth'
import './styles.css'

const app = createApp(App).use(createPinia()).use(router)

// A session that ended elsewhere: forget the user, and ask them to sign in again,
// coming back to where they were.
onUnauthorized(() => {
  useAuthStore().sessionEnded()
  const current = router.currentRoute.value
  if (current.name !== 'login') {
    void router.push({ name: 'login', query: { redirect: current.fullPath } })
  }
})

app.mount('#app')

// Live updates for everyone; the page works without them if the hub is unreachable.
startHub().catch(() => undefined)
