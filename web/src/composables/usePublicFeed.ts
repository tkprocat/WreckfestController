import { onBeforeUnmount, onMounted, ref } from 'vue'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { onHub } from '@/realtime/hub'

export type PublicFeedItem = components['schemas']['PublicFeedItem']
export type PublicCupRun = components['schemas']['PublicCupRun']
export type PublicCupStanding = components['schemas']['PublicCupStanding']
export type PublicRace = components['schemas']['PublicRace']
export type PublicRaceEntry = components['schemas']['PublicRaceEntry']

/** Lets the save settle and a burst of events collapse into one request. */
const REFETCH_DELAY_MS = 1500

/** A slow fallback for when the hub is unreachable; races end minutes apart. */
const POLL_INTERVAL_MS = 300_000

/**
 * What changes the feed: a race saved, or a cup beginning, starting or ending. After a
 * reconnect any of those may have been missed.
 */
const EVENTS = ['RaceRecorded', 'CupActivated', 'CupStarted', 'CupEnded', 'Reconnected'] as const

/** /api/public/feed, reloaded when the hub reports a race or a cup change. */
export function usePublicFeed() {
  const items = ref<PublicFeedItem[] | null>(null)

  /** Why the last load failed. Kept alongside the last good list, which may be stale. */
  const error = ref<string | null>(null)
  const loading = ref(true)

  let refetchTimer: ReturnType<typeof setTimeout> | undefined
  let pollTimer: ReturnType<typeof setInterval> | undefined
  let unsubscribe: (() => void)[] = []

  // Loads can overlap and finish out of order; an answer older than one applied is dropped.
  let started = 0
  let settled = 0

  async function load(): Promise<void> {
    const id = ++started
    let data: PublicFeedItem[] | undefined
    let failure: string | null = null
    try {
      const result = await api.GET('/api/public/feed')
      data = result.data
      if (!data) {
        failure =
          result.response.status === 429
            ? 'Too many requests; recent results will catch up shortly.'
            : 'Recent results could not be loaded.'
      }
    } catch {
      failure = 'Recent results could not be loaded.'
    }

    if (id < settled) {
      return
    }

    settled = id
    loading.value = false
    error.value = failure
    if (data) {
      items.value = data
    }
  }

  onMounted(() => {
    void load()
    unsubscribe = EVENTS.map((event) =>
      onHub(event, () => {
        clearTimeout(refetchTimer)
        refetchTimer = setTimeout(() => void load(), REFETCH_DELAY_MS)
      }),
    )
    pollTimer = setInterval(() => void load(), POLL_INTERVAL_MS)
  })

  onBeforeUnmount(() => {
    clearTimeout(refetchTimer)
    clearInterval(pollTimer)
    unsubscribe.forEach((stop) => stop())
    unsubscribe = []
  })

  return { items, error, loading, reload: load }
}
