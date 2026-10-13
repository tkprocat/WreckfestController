import { readonly, ref } from 'vue'
import { api } from '@/api/client'

// Whether the public AI assistant is turned on and has a key: the header shows its link
// only then. One answer for the whole app, fetched once; refresh() after a settings change.
const enabled = ref(false)
let request: Promise<void> | null = null

async function fetchStatus(): Promise<void> {
  try {
    const { data } = await api.GET('/api/public/assistant')
    enabled.value = data?.enabled ?? false
  } catch {
    enabled.value = false
  }
}

export function useAssistantStatus() {
  return {
    enabled: readonly(enabled),
    /** Fetches the status, or joins the fetch already in flight. Never throws. */
    load(): Promise<void> {
      request ??= fetchStatus().finally(() => {
        request = null
      })
      return request
    },
  }
}
