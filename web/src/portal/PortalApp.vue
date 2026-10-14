<script setup lang="ts">
import { watchEffect } from 'vue'
import StatusBadge from '@/components/StatusBadge.vue'
import GameText from '@/components/GameText.vue'
import { useAppearance } from '@/composables/useAppearance'
import { formatFromNow } from '@/utils/format'
import { useServers, type ServerCard } from './useServers'

const { preference, setPreference } = useAppearance()
const { page, stale, loading, reload } = useServers()

watchEffect(() => {
  if (page.value) document.title = page.value.title
})

const badges: Record<ServerCard['state'], { tone: 'positive' | 'negative' | 'neutral'; label: string }> = {
  checking: { tone: 'neutral', label: 'CHECKING' },
  online: { tone: 'positive', label: 'UP' },
  'server-stopped': { tone: 'negative', label: 'DOWN' },
  unreachable: { tone: 'negative', label: 'NO CONNECTION' },
}

/** Whether the card has anything the controller said, now or before it went away. */
function seen(server: ServerCard): boolean {
  return server.lastSeenAt != null
}

function occupancy(server: ServerCard): string {
  const count = (n: number, label: string) => n + ' ' + label + (n === 1 ? '' : 's')
  return count(server.humans ?? 0, 'player') + ', ' + count(server.bots ?? 0, 'bot') +
    (server.maxPlayers != null ? ' / ' + server.maxPlayers + ' total' : '')
}

function where(server: ServerCard): string {
  if (server.state === 'server-stopped') return 'Server offline'
  return server.currentTrack ?? 'In lobby'
}
</script>

<template>
  <header class="app-header">
    <span class="app-title"><span class="brand-mark" aria-hidden="true">W</span><span>Wreckfest<span class="brand-subtitle">Servers</span></span></span>
    <label class="appearance-control">
      <span>Theme</span>
      <select :value="preference" @change="setPreference(($event.target as HTMLSelectElement).value)">
        <option value="system">System</option>
        <option value="light">Light</option>
        <option value="dark">Dark</option>
      </select>
    </label>
  </header>

  <main class="portal">
    <h1>{{ page?.title ?? 'Wreckfest servers' }}</h1>
    <p v-if="page?.intro" class="intro">{{ page.intro }}</p>

    <p v-if="stale" class="notice">
      <StatusBadge tone="warning" compact>NOT UPDATING</StatusBadge>
      <span>{{ page ? 'Showing the last known status.' : 'The server list could not be loaded.' }}</span>
      <button type="button" class="retry" @click="reload()">Try again</button>
    </p>

    <p v-if="loading && !page" role="status" class="muted">Loading servers…</p>

    <ul v-if="page" class="servers">
      <li v-for="server in page.servers" :key="server.id" class="server">
        <div class="server-heading">
          <h2><a :href="server.link">{{ server.name }}</a></h2>
          <StatusBadge :tone="badges[server.state].tone" compact>{{ badges[server.state].label }}</StatusBadge>
        </div>
        <p v-if="server.description" class="description">{{ server.description }}</p>

        <template v-if="seen(server)">
          <p v-if="server.state === 'unreachable' && server.lastSeenAt" class="stale-label">
            Last known · {{ formatFromNow(server.lastSeenAt) }}
          </p>
          <p v-if="server.serverName" class="server-name"><GameText :text="server.serverName" /></p>
          <dl class="facts">
            <div><dt>Track</dt><dd>{{ where(server) }}</dd></div>
            <div><dt>Players</dt><dd>{{ occupancy(server) }}</dd></div>
          </dl>
        </template>
        <p v-else-if="server.state === 'unreachable'" class="muted unanswered">The server's controller is not answering.</p>

        <a :href="server.link" class="open" :aria-label="'Open ' + server.name">Open →</a>
      </li>
    </ul>
  </main>
</template>

<style scoped>
.portal { box-sizing: border-box; max-width: var(--content-wide); margin: 0 auto; padding: var(--space-10) clamp(16px, 3vw, 48px); }
h1 { margin: 0; font-size: var(--font-title); line-height: 1.2; }
.intro { margin: var(--space-3) 0 0; color: var(--text-secondary); }
.notice { display: flex; flex-wrap: wrap; align-items: center; gap: var(--space-3); margin: var(--space-6) 0 0; }
.retry { font: inherit; font-size: var(--font-control); color: var(--page-text); background: var(--surface); border: 1px solid var(--border-color); border-radius: var(--radius-control); min-height: 36px; padding: 4px 14px; cursor: pointer; }
.servers { list-style: none; margin: var(--space-8) 0 0; padding: 0; display: grid; gap: var(--space-6); grid-template-columns: repeat(auto-fill, minmax(min(100%, 360px), 1fr)); }
.server { display: flex; flex-direction: column; gap: var(--space-3); padding: var(--panel-padding); background: var(--surface); border: 1px solid var(--border-color); border-radius: var(--radius-panel); min-width: 0; }
.server-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-3); }
h2 { margin: 0; font-size: var(--font-section); line-height: 1.2; }
h2 a { color: inherit; text-decoration: none; }
h2 a:hover { color: var(--accent); }
.description, .stale-label, .unanswered { margin: 0; color: var(--text-secondary); }
.stale-label { font-weight: 600; }
.server-name { margin: 0; font-size: var(--font-item); font-weight: 700; overflow-wrap: anywhere; }
.facts { margin: 0; display: grid; gap: var(--space-2); }
.facts div { display: flex; gap: var(--space-3); }
.facts dt { flex: 0 0 5.5em; color: var(--text-secondary); font-size: var(--font-label); padding-top: 2px; }
.facts dd { margin: 0; overflow-wrap: anywhere; }
.open { margin-top: auto; align-self: flex-start; font-weight: 700; padding-top: var(--space-2); }
@media (max-width: 599px) {
  .portal { padding: var(--space-6) var(--space-4); }
  .server { padding: 20px; }
}
</style>
