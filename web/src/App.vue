<script setup lang="ts">
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import { darkTheme, NButton, NConfigProvider, NDialogProvider, NMessageProvider, useOsTheme } from 'naive-ui'
import { restartHub } from '@/realtime/hub'
import { useAuthStore } from '@/stores/auth'

// Follow the operating system's light or dark setting.
const osTheme = useOsTheme()
const theme = computed(() => (osTheme.value === 'dark' ? darkTheme : null))

const auth = useAuthStore()
const router = useRouter()

async function signOut() {
  await auth.logout()
  await restartHub().catch(() => undefined)
  await router.push({ name: 'home' })
}
</script>

<template>
  <NConfigProvider :theme="theme">
    <NMessageProvider>
      <NDialogProvider>
        <header class="app-header">
          <RouterLink to="/" class="app-title">Wreckfest Controller</RouterLink>
          <nav class="app-user">
            <template v-if="auth.authenticated">
              <span>{{ auth.user?.displayName ?? auth.user?.userName }}</span>
              <NButton size="small" quaternary @click="signOut">Sign out</NButton>
            </template>
            <RouterLink v-else to="/login">Sign in</RouterLink>
          </nav>
        </header>
        <main class="app-main">
          <RouterView />
        </main>
      </NDialogProvider>
    </NMessageProvider>
  </NConfigProvider>
</template>
