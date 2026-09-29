import { ref } from 'vue'
import { api } from '@/api/client'
import { problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'

export type ServerStatus = components['schemas']['ServerStatusResponse']

/**
 * /api/server/status, for a page that acts on it. Loads can overlap (the page opening, a
 * hub event) and finish out of order, so each is numbered and an older answer is dropped.
 * When a load fails the state is unknown - null - rather than the last one seen: a page
 * must not offer Start or Update to a server it cannot see.
 */
export function useServerStatus() {
  const status = ref<ServerStatus | null>(null)
  const error = ref<string | null>(null)

  let started = 0
  let settled = 0

  async function load(): Promise<void> {
    const id = ++started
    let data: ServerStatus | undefined
    let failure: string | null = null
    try {
      const result = await api.GET('/api/server/status')
      data = result.data
      if (!data) {
        failure = problemMessage(result.error, 'The server status could not be loaded.')
      }
    } catch {
      failure = 'The controller cannot be reached.'
    }

    if (id < settled) {
      return
    }

    settled = id
    error.value = failure
    status.value = data ?? null
  }

  return { status, error, load }
}
