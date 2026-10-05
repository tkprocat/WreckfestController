<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NButton, NConfigProvider, NDialogProvider, NMessageProvider } from 'naive-ui'
import { useAuthStore } from '@/stores/auth'
import { useAppearance } from '@/composables/useAppearance'

const { preference, theme, overrides, setPreference } = useAppearance()

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()
const isAdmin = computed(() => route.matched.some(record => record.meta.requiresAuth))

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
  <NConfigProvider :theme="theme" :theme-overrides="overrides">
    <NMessageProvider to="#messages">
      <NDialogProvider>
        <a href="#main-content" class="skip-link">Skip to content</a>
        <header class="app-header">
          <RouterLink to="/" class="app-title" aria-label="Wreckfest Controller"><span class="brand-mark" aria-hidden="true">W</span><span>Wreckfest<span class="brand-subtitle">Controller</span></span></RouterLink>
          <nav class="app-user">
            <label class="appearance-control">
              <span>Theme</span>
              <select :value="preference" @change="setPreference(($event.target as HTMLSelectElement).value)">
                <option value="system">System</option>
                <option value="light">Light</option>
                <option value="dark">Dark</option>
              </select>
            </label>
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
        <main id="main-content" class="app-main" :class="{ 'app-main-admin': isAdmin, 'app-main-spectator': route.name === 'home' }" tabindex="-1">
          <RouterView />
        </main>
      </NDialogProvider>
    </NMessageProvider>
  </NConfigProvider>
</template>
