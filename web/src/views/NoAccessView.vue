<script setup lang="ts">
import { onMounted } from 'vue'
import { useRouter } from 'vue-router'
import { NButton, NResult } from 'naive-ui'
import { useManagedServers } from '@/composables/useManagedServers'
import { useAuthStore } from '@/stores/auth'

// Signed in, but this server's control room is not theirs: signing in again would not help.
const auth = useAuthStore()
const router = useRouter()
const { servers, load } = useManagedServers()
onMounted(load)

async function signOut() {
  if (await auth.logout()) {
    await router.replace({ name: 'login' })
  }
}
</script>

<template>
  <NResult
    status="403"
    title="Not your control room"
    :description="auth.user
      ? `You are signed in as ${auth.user.displayName || auth.user.userName}, but you cannot manage this server. Ask an admin to give you access.`
      : 'You cannot manage this server.'"
  >
    <template #footer>
      <div class="actions">
        <a v-for="server in servers" :key="server.id" class="server-link" :href="server.link + 'admin'">
          Go to {{ server.name }}
        </a>
        <RouterLink class="home-link" to="/">Back to Home</RouterLink>
        <NButton v-if="auth.authenticated" quaternary @click="signOut">Sign out</NButton>
      </div>
    </template>
  </NResult>
</template>

<style scoped>
.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  justify-content: center;
}
.home-link,
.server-link {
  display: inline-flex;
  align-items: center;
  min-height: 36px;
  padding: 0 16px;
  border-radius: var(--radius-control);
  font-weight: 600;
  text-decoration: none;
}
.home-link {
  background: var(--accent);
  color: var(--surface);
}
.server-link {
  border: 1px solid var(--border-color);
  color: var(--page-text);
}
.home-link:hover,
.server-link:hover { opacity: .9; }
</style>
