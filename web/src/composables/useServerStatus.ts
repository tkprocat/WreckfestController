import { computed, ref } from 'vue'
import { api } from '@/api/client'
import { problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'

export type ServerStatus = components['schemas']['ServerStatusResponse']

/** A status request that has not answered by then counts as failed: the state is unknown. */
const TIMEOUT_MS = 10_000

/**
 * /api/server/status, for a page that shows it and a page that acts on it.
 *
 * Loads can overlap (the page opening, hub events) and finish out of order. Answers are
 * applied in the order their loads started - an answer older than one already applied is
 * dropped - so the page always moves forward, however often loads start.
 *
 * But an answer can be out of date by the time it arrives: a later load was started
 * because something happened. So <see>refreshing</see> is true while any load is still on
 * its way, and a page that acts on the state must wait for it to be false. A load that
 * does not answer within the timeout fails, so this can never stay true for good.
 *
 * When a load fails the state is unknown - null - rather than the last one seen: a page
 * must not offer Start or Update to a server it cannot see.
 */
export function useServerStatus() {
  const status = ref<ServerStatus | null>(null)
  const error = ref<string | null>(null)
  const inFlight = ref(0)
  const refreshing = computed(() => inFlight.value > 0)

  let started = 0
  let applied = 0

  async function load(): Promise<void> {
    const id = ++started
    inFlight.value++
    let data: ServerStatus | undefined
    let failure: string | null = null
    try {
      const result = await api.GET('/api/server/status', { signal: AbortSignal.timeout(TIMEOUT_MS) })
      data = result.data
      if (!data) {
        failure = problemMessage(result.error, 'The server status could not be loaded.')
      }
    } catch {
      failure = 'The controller cannot be reached.'
    } finally {
      inFlight.value--
    }

    if (id < applied) {
      return
    }

    applied = id
    error.value = failure
    status.value = data ?? null
  }

  return { status, error, refreshing, load }
}
