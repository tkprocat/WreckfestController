import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api, refreshAntiforgery } from '@/api/client'
import type { components } from '@/api/schema'
import { restartHub } from '@/realtime/hub'

export type User = components['schemas']['UserResponse']

/** Why a sign-in failed, in words for the sign-in page. */
export type LoginFailure = 'invalid' | 'locked' | 'rate-limited' | 'unavailable'

/**
 * Who is signed in, from /api/auth/state, and signing in and out. Every change of who is
 * signed in goes through here, and each one ends the same way (see settle): a fresh
 * antiforgery token for the new user, and a hub connection regrouped for them.
 */
export const useAuthStore = defineStore('auth', () => {
  const user = ref<User | null>(null)
  const setupRequired = ref(false)

  /** The controller's database is unavailable: nobody can sign in until it is fixed. */
  const degraded = ref(false)

  /** Whether /api/auth/state has answered at least once. */
  const loaded = ref(false)

  /** The last /api/auth/state request failed to reach the controller, or it answered an error. */
  const unreachable = ref(false)

  const authenticated = computed(() => user.value !== null)

  /**
   * Asks who is signed in. Never throws: when the controller cannot be reached, it
   * records that, leaves loaded false so the next navigation asks again, and returns false.
   */
  async function load(): Promise<boolean> {
    try {
      const { data } = await api.GET('/api/auth/state')
      if (!data) {
        unreachable.value = true
        return false
      }

      user.value = data.user
      setupRequired.value = data.setupRequired
      degraded.value = data.degraded
      unreachable.value = false
      loaded.value = true
      return true
    } catch {
      unreachable.value = true
      return false
    }
  }

  async function login(login: string, password: string, remember: boolean): Promise<LoginFailure | null> {
    const { data, response } = await api.POST('/api/auth/login', { body: { login, password, remember } })
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
   * Signs out. Returns false - and keeps the user - when the server did not confirm it:
   * the session cookie may still be valid, so the page must not claim otherwise.
   */
  async function logout(): Promise<boolean> {
    const { response } = await api.POST('/api/auth/logout')
    if (!response.ok) {
      return false
    }

    user.value = null
    await settle()
    return true
  }

  /**
   * The session ended elsewhere (a 401): forget the user without asking the server, and
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
    degraded,
    loaded,
    unreachable,
    authenticated,
    load,
    login,
    logout,
    sessionEnded,
  }
})
