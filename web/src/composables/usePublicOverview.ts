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
  'CupStarted',
  'CupEnded',
  'ServerStarted',
  'ServerStopped',
  'ServerRestarted',
  'ServerAttached',
]

type Players = PublicOverview['players']

/** /api/public/overview, kept current by the hub's public group. */
export function usePublicOverview() {
  const overview = ref<PublicOverview | null>(null)

  /** Why the last load failed. Kept alongside the last good overview, which may be stale. */
  const error = ref<string | null>(null)
  const loading = ref(true)

  let refetchTimer: ReturnType<typeof setTimeout> | undefined
  let pollTimer: ReturnType<typeof setInterval> | undefined
  const unsubscribe: (() => void)[] = []

  // Loads can overlap (the poll, a debounced refetch) and finish out of order. Each gets a
  // number; an answer older than one already applied is dropped.
  let started = 0
  let settled = 0

  // The players the hub last reported, and how many loads had started by then: a load
  // among those may have been answered before that update, so its players are older.
  let hubPlayers: { players: Players; afterLoad: number } | null = null

  async function load(): Promise<void> {
    const id = ++started
    let data: PublicOverview | undefined
    let failure: string | null = null
    try {
      const result = await api.GET('/api/public/overview')
      data = result.data
      if (!data) {
        failure =
          result.response.status === 503
            ? "The controller's database is unavailable."
            : result.response.status === 429
              ? 'Too many requests; this page will catch up shortly.'
              : 'The server overview could not be loaded.'
      }
    } catch {
      failure = 'The controller cannot be reached.'
    }

    if (id < settled) {
      return
    }

    settled = id
    loading.value = false
    error.value = failure
    if (data) {
      if (hubPlayers && hubPlayers.afterLoad >= id) {
        data.players = hubPlayers.players
      }

      overview.value = data
    }
  }

  function refetchSoon(): void {
    clearTimeout(refetchTimer)
    refetchTimer = setTimeout(() => void load(), REFETCH_DELAY_MS)
  }

  onMounted(() => {
    void load()

    unsubscribe.push(
      onHub('PlayersUpdated', ({ players }) => {
        const current: Players = {
          humans: players.filter((p) => !p.isBot).length,
          bots: players.filter((p) => p.isBot).length,
          list: players.map((p) => ({ name: p.name, isBot: p.isBot })),
        }

        // Kept even before the first load answers, so that answer cannot undo it.
        hubPlayers = { players: current, afterLoad: started }
        if (overview.value) {
          overview.value.players = current
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
