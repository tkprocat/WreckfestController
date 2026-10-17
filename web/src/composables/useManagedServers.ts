import { computed, ref } from 'vue'
import { portal } from '@/api/client'
import { SERVER_ID } from '@/base'
import { canManage, useAuthStore } from '@/stores/auth'

/** A server on the portal, by its id and name, and where its site is. */
export interface SiteServer {
  id: string
  name: string
  link: string
  /** The server this page belongs to. */
  current: boolean
}

/**
 * The portal's servers the signed-in user may manage, for moving between their control
 * rooms. Fetched once; empty until it answers, or when it cannot.
 */
export function useManagedServers() {
  const auth = useAuthStore()
  const all = ref<SiteServer[]>([])

  async function load(): Promise<void> {
    try {
      const { data } = await portal.GET('/api/servers')
      all.value = (data?.servers ?? []).map((s) => ({
        id: s.id,
        name: s.name,
        link: s.link,
        current: s.id.toLowerCase() === SERVER_ID.toLowerCase(),
      }))
    } catch {
      all.value = []
    }
  }

  const servers = computed(() => all.value.filter((s) => canManage(auth.user, s.id)))

  /** Every server, for an admin choosing what a user may manage. */
  const everyServer = computed(() => all.value)

  return { servers, everyServer, load }
}
