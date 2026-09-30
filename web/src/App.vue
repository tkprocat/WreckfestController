<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { darkTheme, NButton, NConfigProvider, NDialogProvider, NMessageProvider, useOsTheme } from 'naive-ui'
import { useAuthStore } from '@/stores/auth'

// Follow the operating system's light or dark setting.
const osTheme = useOsTheme()
const theme = computed(() => (osTheme.value === 'dark' ? darkTheme : null))

const auth = useAuthStore()
const router = useRouter()

/** Shown when the server did not confirm the sign-out: the session may still be valid. */
const signOutFailed = ref(false)

async function signOut() {
  signOutFailed.value = !(await auth.logout().catch(() => false))
  if (!signOutFailed.value) {
    await router.push({ name: 'home' })
  }
}
</script>

<template>
  <NConfigProvider :theme="theme">
    <NMessageProvider to="#messages">
      <NDialogProvider>
        <header class="app-header">
          <RouterLink to="/" class="app-title">Wreckfest Controller</RouterLink>
          <nav class="app-user">
            <template v-if="auth.authenticated">
              <RouterLink :to="{ name: 'admin-dashboard' }">Admin</RouterLink>
              <RouterLink :to="{ name: 'admin-profile' }" title="Your profile">
                {{ auth.user?.displayName ?? auth.user?.userName }}
              </RouterLink>
              <span v-if="signOutFailed" class="app-error">Sign-out failed. Try again.</span>
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
