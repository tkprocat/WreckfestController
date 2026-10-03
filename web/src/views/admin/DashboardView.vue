<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import { computed, h, onBeforeUnmount, onMounted, ref } from 'vue'
import { NAlert, NCard, NDataTable, NStatistic, NTag, type DataTableColumns } from 'naive-ui'
import { api } from '@/api/client'
import { problemMessage } from '@/api/problems'
import { useServerStatus } from '@/composables/useServerStatus'
import { onHub, type HubEvents, type PlayerSummary } from '@/realtime/hub'
import { formatUptime } from '@/utils/format'

const { status, error: statusError, refreshing, load: loadStatus } = useServerStatus()
const players = ref<PlayerSummary[]>([])
const playersError = ref<string | null>(null)
const playersLoaded = ref(false)
const playersLoading = ref(true)
/** A row key that says what it is, so player id 3 and slot 3 never share one. */
const rosterKey = (p: PlayerSummary) =>
  p.playerId != null ? `id:${p.playerId}` : p.slot != null ? `slot:${p.slot}` : `name:${p.name}`

// A roster from the hub is newer than a load that was on its way when it arrived: the
// load's answer must not put the older roster back. A failed load keeps what is shown.
let hubRosterSeen = false

async function loadPlayers() {
  hubRosterSeen = false
  playersLoading.value = true
  try {
    const { data, error: problem } = await api.GET('/api/server/players')
    if (!data) {
      if (!hubRosterSeen) playersError.value = problemMessage(problem, 'The players could not be loaded.')
      return
    }

    if (hubRosterSeen) return
    playersError.value = null
    players.value = (data.players ?? []).map((p) => ({
      name: p.name ?? '',
      playerId: p.playerId ?? null,
      score: p.score ?? null,
      vehicle: p.vehicle ?? null,
      slot: p.slot ?? null,
      isBot: p.isBot ?? false,
      joinedAt: p.joinedAt ?? '',
    }))
    playersLoaded.value = true
  } catch {
    if (!hubRosterSeen) playersError.value = 'The players could not be loaded: no answer from the controller.'
  } finally {
    playersLoading.value = false
  }
}

const statusEvents: (keyof HubEvents)[] = ['ServerStarted', 'ServerStopped', 'ServerRestarted', 'ServerAttached', 'TrackChanged']
const stops: (() => void)[] = []
let uptimeTimer: ReturnType<typeof setInterval> | undefined

onMounted(() => {
  void loadStatus()
  void loadPlayers()
  stops.push(
    ...statusEvents.map((event) => onHub(event, () => void loadStatus())),
    onHub('PlayersUpdated', ({ players: list }) => {
      hubRosterSeen = true
      players.value = list
      playersLoaded.value = true
      playersLoading.value = false
      playersError.value = null
    }),
  )

  // Uptime moves on between events.
  uptimeTimer = setInterval(() => {
    if (status.value?.isRunning && status.value.uptimeSeconds != null) {
      status.value.uptimeSeconds += 1
    }
  }, 1000)
})

onBeforeUnmount(() => {
  stops.forEach((stop) => stop())
  clearInterval(uptimeTimer)
})

const humans = computed(() => players.value.filter((p) => !p.isBot).length)
const bots = computed(() => players.value.length - humans.value)
const serverLabel = computed(() =>
  refreshing.value ? 'Checking…' : status.value === null ? 'Unknown' : status.value.isRunning ? 'Running' : 'Stopped',
)
const uptimeLabel = computed(() =>
  refreshing.value || status.value === null ? 'Unknown' : status.value.isRunning ? formatUptime(status.value.uptimeSeconds) || 'Unknown' : '—',
)
const playerLabel = computed(() =>
  playersLoaded.value ? String(humans.value) : playersLoading.value ? 'Loading…' : 'Unknown',
)
const trackLabel = computed(() =>
  status.value === null ? 'Track unavailable' : status.value.isRunning ? status.value.currentTrack || 'Between races' : 'Server stopped',
)

const columns: DataTableColumns<PlayerSummary> = [
  { title: 'Slot', key: 'slot', width: 70, sorter: (a, b) => (a.slot ?? 99) - (b.slot ?? 99) },
  {
    title: 'Player',
    key: 'name',
    sorter: (a, b) => a.name.localeCompare(b.name),
    render: (p) => h('span', { class: 'player-identity' }, [
      h('strong', null, p.name),
      p.isBot ? h(NTag, { size: 'small', bordered: false }, { default: () => 'Bot' }) : null,
    ]),
  },
  { title: 'Vehicle', key: 'vehicle', render: (p) => h('span', { class: 'vehicle-name' }, p.vehicle ?? '—') },
  { title: 'Score', key: 'score', width: 90, sorter: (a, b) => (a.score ?? 0) - (b.score ?? 0), render: (p) => p.score ?? '—' },
]
</script>

<template>
  <section>
    <PageHeader title="Dashboard" description="Your server at a glance.">
      <template #actions><RouterLink class="control-link" to="/admin/server">Open server control →</RouterLink></template>
    </PageHeader>
    <NAlert v-if="statusError" type="warning" :title="statusError" class="gap">Server status is unknown until the controller answers.</NAlert>
    <NAlert v-if="playersError" type="warning" :title="playersError" class="gap">
      {{ playersLoaded ? 'Showing the last confirmed roster; it may be out of date.' : 'The player count is unknown.' }}
    </NAlert>

    <div class="metrics">
      <NCard class="metric-card">
        <NStatistic label="Server">
          <NTag :type="!refreshing && status?.isRunning ? 'success' : 'default'" round>{{ serverLabel }}</NTag>
        </NStatistic>
      </NCard>
      <NCard class="metric-card"><NStatistic label="Uptime" :value="uptimeLabel" /></NCard>
      <NCard class="metric-card">
        <NStatistic label="Human players" :value="playerLabel">
          <template #suffix><span v-if="playersLoaded && bots" class="metric-suffix">+ {{ bots }} bots</span></template>
        </NStatistic>
      </NCard>
    </div>

    <NCard class="track-card">
      <p class="eyebrow">{{ refreshing || statusError ? 'Last known track' : 'Current track' }}</p>
      <h2>{{ trackLabel }}</h2>
      <p v-if="status?.isRunning && status.currentTrack" class="track-caption">Now on the server</p>
    </NCard>

    <NCard class="roster-card">
      <div class="roster-heading">
        <div><p class="eyebrow">Connected drivers</p><h2>Players</h2></div>
        <span v-if="playersLoaded" class="roster-count">{{ players.length }} total</span>
      </div>
      <p class="table-hint">Scroll the table for vehicle and score.</p>
      <NDataTable
        :scroll-x="620"
        :columns="columns"
        :data="players"
        :loading="playersLoading && !playersLoaded"
        :row-key="rosterKey"
        :bordered="false"
        :pagination="players.length > 10 ? { pageSize: 10 } : false"
        size="medium"
      >
        <template #empty>{{ playersLoading ? 'Loading players…' : playersError ? 'Player roster unavailable.' : 'No players connected.' }}</template>
      </NDataTable>
    </NCard>
  </section>
</template>

<style scoped>
.gap { margin-bottom: 16px; }
.metrics { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 16px; margin-bottom: 16px; }
.metric-card { min-width: 0; }
.metric-suffix { margin-left: 8px; color: var(--text-muted); font-size: 12px; }
.track-card { margin-bottom: 16px; min-width: 0; }
.eyebrow { margin: 0; color: var(--text-muted); font-size: 11px; font-weight: 700; letter-spacing: .12em; text-transform: uppercase; }
.track-card h2 { margin: 10px 0 0; font-size: clamp(25px, 3vw, 38px); line-height: 1.15; letter-spacing: -.03em; overflow-wrap: anywhere; }
.track-caption { margin: 12px 0 0; color: var(--text-muted); }
.roster-heading { display: flex; justify-content: space-between; align-items: center; gap: 12px; margin-bottom: 12px; }
.roster-heading h2 { margin: 3px 0 0; font-size: 20px; }
.roster-count { color: var(--text-muted); font-size: 12px; }
.table-hint { display: none; margin: 0 0 8px; color: var(--text-muted); font-size: 12px; }
.control-link { align-self: center; color: var(--accent); font-weight: 600; text-decoration: none; }
.control-link:hover { text-decoration: underline; }
:deep(.player-identity) { display: inline-flex; align-items: center; flex-wrap: wrap; gap: 6px; min-width: 0; overflow-wrap: anywhere; }
:deep(.vehicle-name) { overflow-wrap: anywhere; }
@media (max-width: 600px) { .table-hint { display: block; } .metrics { grid-template-columns: 1fr 1fr; } .metric-card:last-child { grid-column: 1 / -1; } }
</style>
