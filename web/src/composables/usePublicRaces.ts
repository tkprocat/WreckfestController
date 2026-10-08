import { onBeforeUnmount, onMounted, ref } from 'vue'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { onHub } from '@/realtime/hub'

export type PublicRace = components['schemas']['PublicRace']
export type PublicRaceEntry = components['schemas']['PublicRaceEntry']

/** Lets the save settle and a burst of events collapse into one request. */
const REFETCH_DELAY_MS = 1500

/** A slow fallback for when the hub is unreachable; races end minutes apart. */
const POLL_INTERVAL_MS = 300_000

/** /api/public/races, reloaded when the hub reports a newly recorded race. */
export function usePublicRaces() {
  const races = ref<PublicRace[] | null>(null)

  /** Why the last load failed. Kept alongside the last good list, which may be stale. */
  const error = ref<string | null>(null)
  const loading = ref(true)

  let refetchTimer: ReturnType<typeof setTimeout> | undefined
  let pollTimer: ReturnType<typeof setInterval> | undefined
  let unsubscribe: (() => void) | undefined

  // Loads can overlap and finish out of order; an answer older than one applied is dropped.
  let started = 0
  let settled = 0

  async function load(): Promise<void> {
    const id = ++started
    let data: PublicRace[] | undefined
    let failure: string | null = null
    try {
      const result = await api.GET('/api/public/races')
      data = result.data
      if (!data) {
        failure =
          result.response.status === 429
            ? 'Too many requests; recent races will catch up shortly.'
            : 'Recent races could not be loaded.'
      }
    } catch {
      failure = 'Recent races could not be loaded.'
    }

    if (id < settled) {
      return
    }

    settled = id
    loading.value = false
    error.value = failure
    if (data) {
      races.value = data
    }
  }

  onMounted(() => {
    void load()
    unsubscribe = onHub('RaceRecorded', () => {
      clearTimeout(refetchTimer)
      refetchTimer = setTimeout(() => void load(), REFETCH_DELAY_MS)
    })
    pollTimer = setInterval(() => void load(), POLL_INTERVAL_MS)
  })

  onBeforeUnmount(() => {
    clearTimeout(refetchTimer)
    clearInterval(pollTimer)
    unsubscribe?.()
  })

  return { races, error, loading, reload: load }
}
