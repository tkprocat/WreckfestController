import { onScopeDispose, ref } from 'vue'

/** How a server looked at the portal's last check (Portal/WreckfestPortal/ServerStatus.cs). */
export type ServerState = 'checking' | 'online' | 'server-stopped' | 'unreachable'

export interface ServerCard {
  id: string
  name: string
  description: string | null
  link: string
  state: ServerState
  serverName: string | null
  humans: number | null
  bots: number | null
  maxPlayers: number | null
  currentTrack: string | null
  checkedAt: string | null
  lastSeenAt: string | null
}

export interface PortalPage {
  title: string
  intro: string | null
  servers: ServerCard[]
}

/** As often as the portal checks the servers by default. */
export const REFRESH_MS = 15_000

/**
 * The portal's server list, fetched now and every {@link REFRESH_MS}. A failed refresh
 * keeps what was shown, with `stale` set, so the page says it is not updating.
 */
export function useServers(fetcher: typeof fetch = (...args) => fetch(...args)) {
  const page = ref<PortalPage | null>(null)
  const stale = ref(false)
  const loading = ref(true)

  async function load() {
    try {
      const response = await fetcher('/api/servers', { signal: AbortSignal.timeout(10_000) })
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      page.value = (await response.json()) as PortalPage
      stale.value = false
    } catch {
      stale.value = true
    } finally {
      loading.value = false
    }
  }

  void load()
  const timer = setInterval(() => void load(), REFRESH_MS)
  onScopeDispose(() => clearInterval(timer))

  return { page, stale, loading, reload: load }
}
