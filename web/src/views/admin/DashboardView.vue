<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import { computed, h, onBeforeUnmount, onMounted, ref } from 'vue'
import { NAlert, NCard, NDataTable, NGi, NGrid, NStatistic, NTag, type DataTableColumns } from 'naive-ui'
import { api } from '@/api/client'
import { problemMessage } from '@/api/problems'
import { useServerStatus } from '@/composables/useServerStatus'
import { onHub, type HubEvents, type PlayerSummary } from '@/realtime/hub'
import { formatUptime } from '@/utils/format'

const { status, error: statusError, load: loadStatus } = useServerStatus()
const players = ref<PlayerSummary[]>([])
const playersError = ref<string | null>(null)
const error = computed(() => statusError.value ?? playersError.value)

// A roster from the hub is newer than a load that was on its way when it arrived: the
// load's answer must not put the older roster back. A failed load keeps what is shown.
let hubRosterSeen = false

async function loadPlayers() {
  hubRosterSeen = false
  try {
    const { data, error: problem } = await api.GET('/api/server/players')
    if (!data) {
      playersError.value = problemMessage(problem, 'The players could not be loaded.')
      return
    }

    playersError.value = null
    if (hubRosterSeen) {
      return
    }

    players.value = (data.players ?? []).map((p) => ({
      name: p.name ?? '',
      playerId: p.playerId ?? null,
      score: p.score ?? null,
      vehicle: p.vehicle ?? null,
      slot: p.slot ?? null,
      isBot: p.isBot ?? false,
      joinedAt: p.joinedAt ?? '',
    }))
  } catch {
    playersError.value = 'The players could not be loaded: no answer from the controller.'
  }
}

// The hub says when something changed; the status endpoint says what it is now.
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

const columns: DataTableColumns<PlayerSummary> = [
  { title: 'Slot', key: 'slot', width: 70, sorter: (a, b) => (a.slot ?? 99) - (b.slot ?? 99) },
  {
    title: 'Name',
    key: 'name',
    sorter: (a, b) => a.name.localeCompare(b.name),
    render: (p) => (p.isBot ? [p.name, ' ', h(NTag, { size: 'small', bordered: false }, { default: () => 'bot' })] : p.name),
  },
  { title: 'Vehicle', key: 'vehicle', render: (p) => p.vehicle ?? '' },
  { title: 'Score', key: 'score', width: 90, sorter: (a, b) => (a.score ?? 0) - (b.score ?? 0), render: (p) => p.score ?? '' },
]
</script>

<template>
  <section>
    <PageHeader title="Dashboard" description="Your server at a glance." />
    <NAlert v-if="error" type="warning" :title="error" class="gap" />

    <NGrid cols="2 m:4" responsive="screen" :x-gap="16" :y-gap="16" class="gap">
      <NGi>
        <NCard>
          <NStatistic label="Server">
            <NTag :type="status?.isRunning ? 'success' : 'default'" round>
              {{ status === null ? 'Unknown' : status.isRunning ? 'Running' : 'Stopped' }}
            </NTag>
          </NStatistic>
        </NCard>
      </NGi>
      <NGi>
        <NCard>
          <NStatistic label="Uptime" :value="status?.isRunning ? formatUptime(status.uptimeSeconds) : '–'" />
        </NCard>
      </NGi>
      <NGi>
        <NCard>
          <NStatistic label="Players" :value="`${humans}`">
            <template #suffix>
              <span v-if="players.length > humans" class="muted"> + {{ players.length - humans }} bots</span>
            </template>
          </NStatistic>
        </NCard>
      </NGi>
      <NGi>
        <NCard>
          <NStatistic label="Track" :value="status?.currentTrack ?? '–'" />
        </NCard>
      </NGi>
    </NGrid>

    <NCard title="Players">
      <NDataTable :scroll-x="600" :columns="columns" :data="players" :row-key="(p: PlayerSummary) => p.name" :bordered="false" size="small" />
    </NCard>
  </section>
</template>

<style scoped>
h1 {
  margin-top: 0;
}

.gap {
  margin-bottom: 16px;
}

.muted {
  color: var(--text-muted);
  font-size: 0.8em;
}
</style>
