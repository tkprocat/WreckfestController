import { onBeforeUnmount, onMounted, ref } from 'vue'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { onHub, type HubEvents } from '@/realtime/hub'

export type PublicOverview = components['schemas']['PublicOverview']

/** Refetches after a burst of hub events settles, well inside the 60-a-minute limit. */
const REFETCH_DELAY_MS = 1500

/** A slow fallback for when the hub is unreachable. */
const POLL_INTERVAL_MS = 60_000

/** Events that change what the overview shows, other than players (applied directly). */
const REFETCH_ON: (keyof HubEvents)[] = [
  'TrackChanged',
  'CupActivated',
  'ServerStarted',
  'ServerStopped',
  'ServerRestarted',
  'ServerAttached',
]

/** /api/public/overview, kept current by the hub's public group. */
export function usePublicOverview() {
  const overview = ref<PublicOverview | null>(null)
  const error = ref<string | null>(null)
  const loading = ref(true)

  let refetchTimer: ReturnType<typeof setTimeout> | undefined
  let pollTimer: ReturnType<typeof setInterval> | undefined
  const unsubscribe: (() => void)[] = []

  async function load(): Promise<void> {
    const { data, response } = await api.GET('/api/public/overview')
    if (data) {
      overview.value = data
      error.value = null
    } else {
      error.value =
        response.status === 503
          ? "The controller's database is unavailable."
          : response.status === 429
            ? 'Too many requests; this page will catch up shortly.'
            : 'The server overview could not be loaded.'
    }

    loading.value = false
  }

  function refetchSoon(): void {
    clearTimeout(refetchTimer)
    refetchTimer = setTimeout(() => void load(), REFETCH_DELAY_MS)
  }

  onMounted(() => {
    void load()

    unsubscribe.push(
      onHub('PlayersUpdated', ({ players }) => {
        if (!overview.value) {
          return
        }

        overview.value.players = {
          humans: players.filter((p) => !p.isBot).length,
          bots: players.filter((p) => p.isBot).length,
          list: players.map((p) => ({ name: p.name, isBot: p.isBot })),
        }
      }),
      ...REFETCH_ON.map((event) => onHub(event, refetchSoon)),
    )

    pollTimer = setInterval(() => void load(), POLL_INTERVAL_MS)
  })

  onBeforeUnmount(() => {
    clearTimeout(refetchTimer)
    clearInterval(pollTimer)
    unsubscribe.forEach((stop) => stop())
  })

  return { overview, error, loading, reload: load }
}
