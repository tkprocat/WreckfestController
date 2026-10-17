import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { portal, refreshAntiforgery } from '@/api/client'
import type { components } from '@/api/portal-schema'
import { SERVER_ID } from '@/base'
import { restartHub } from '@/realtime/hub'

export type User = components['schemas']['UserResponse']

/** Why a sign-in failed, in words for the sign-in page. */
export type LoginFailure = 'invalid' | 'locked' | 'rate-limited' | 'unavailable'

/** Whether `user` may manage the server with `serverId`: a global admin, or a user granted it. */
export function canManage(user: User | null, serverId: string = SERVER_ID): boolean {
  if (user === null) {
    return false
  }

  return user.isAdmin || user.servers.some((id) => id.toLowerCase() === serverId.toLowerCase())
}

/**
 * Who is signed in, from the portal's /api/auth/state, and signing in and out. One sign-in
 * covers every server on the site. Every change of who is signed in goes through here, and
 * each one ends the same way (see settle): a fresh antiforgery token for the new user, and a
 * hub connection regrouped for them.
 */
export const useAuthStore = defineStore('auth', () => {
  const user = ref<User | null>(null)

  /** No account exists yet: the first is made on the portal's PC (create-admin). */
  const setupRequired = ref(false)

  /** Whether /api/auth/state has answered at least once. */
  const loaded = ref(false)

  /** The last /api/auth/state request failed to reach the portal, or it answered an error. */
  const unreachable = ref(false)

  const authenticated = computed(() => user.value !== null)

  /** May manage this server: its admin pages are open to them. */
  const managesThisServer = computed(() => canManage(user.value))

  /** A global admin: every server, and the accounts. */
  const isAdmin = computed(() => user.value?.isAdmin === true)

  /**
   * Asks who is signed in. Never throws: when the portal cannot be reached, it records
   * that, leaves loaded false so the next navigation asks again, and returns false. A
   * different answer than before (the session ended elsewhere) settles like a sign-out.
   */
  async function load(): Promise<boolean> {
    try {
      const { data } = await portal.GET('/api/auth/state')
      if (!data) {
        unreachable.value = true
        return false
      }

      const changed = loaded.value && (user.value?.id ?? null) !== (data.user?.id ?? null)
      user.value = data.user
      setupRequired.value = data.setupRequired
      unreachable.value = false
      loaded.value = true
      if (changed) {
        await settle()
      }

      return true
    } catch {
      unreachable.value = true
      return false
    }
  }

  async function login(login: string, password: string, remember: boolean): Promise<LoginFailure | null> {
    const { data, response } = await portal.POST('/api/auth/login', { body: { login, password, remember } })
    if (data) {
      user.value = data
      await settle()
      return null
    }

    switch (response.status) {
      case 401:
        return 'invalid'
      case 423:
        return 'locked'
      case 429:
        return 'rate-limited'
      default:
        return 'unavailable'
    }
  }

  /**
   * Signs out of the whole site. Returns false - and keeps the user - when the portal did
   * not confirm it: the session cookie may still be valid, so the page must not claim otherwise.
   */
  async function logout(): Promise<boolean> {
    const { response } = await portal.POST('/api/auth/logout')
    if (!response.ok) {
      return false
    }

    user.value = null
    await settle()
    return true
  }

  /** The signed-in user's account changed (their profile): shown everywhere at once. */
  function updated(changed: User): void {
    user.value = changed
  }

  /**
   * The session ended elsewhere (a 401): forget the user without asking the portal, and
   * drop the hub's admin group, which the server only assigns when a connection starts.
   */
  function sessionEnded(): void {
    if (user.value === null) {
      return
    }

    user.value = null
    void settle()
  }

  /**
   * After who is signed in changed. The two steps run side by side: a stalled token
   * fetch must not keep an ended session's hub in the admin group. Neither failing undoes
   * the change: a stale token is refreshed and retried by the client's middleware, and
   * the hub is optional.
   */
  async function settle(): Promise<void> {
    await Promise.all([refreshAntiforgery().catch(() => undefined), restartHub().catch(() => undefined)])
  }

  return {
    user,
    setupRequired,
    loaded,
    unreachable,
    authenticated,
    managesThisServer,
    isAdmin,
    load,
    login,
    logout,
    updated,
    sessionEnded,
  }
})
