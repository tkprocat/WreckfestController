import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { api, refreshAntiforgery } from '@/api/client'
import type { components } from '@/api/schema'

export type User = components['schemas']['UserResponse']

/** Why a sign-in failed, in words for the sign-in page. */
export type LoginFailure = 'invalid' | 'locked' | 'rate-limited' | 'unavailable'

/** Who is signed in, from /api/auth/state, and signing in and out. */
export const useAuthStore = defineStore('auth', () => {
  const user = ref<User | null>(null)
  const setupRequired = ref(false)

  /** The controller's database is unavailable: nobody can sign in until it is fixed. */
  const degraded = ref(false)

  /** Whether /api/auth/state has answered at least once. */
  const loaded = ref(false)

  const authenticated = computed(() => user.value !== null)

  async function load(): Promise<void> {
    const { data } = await api.GET('/api/auth/state')
    if (data) {
      user.value = data.user
      setupRequired.value = data.setupRequired
      degraded.value = data.degraded
    }

    loaded.value = true
  }

  async function login(login: string, password: string, remember: boolean): Promise<LoginFailure | null> {
    const { data, response } = await api.POST('/api/auth/login', { body: { login, password, remember } })
    if (data) {
      user.value = data
      await refreshAntiforgery()
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

  async function logout(): Promise<void> {
    await api.POST('/api/auth/logout')
    user.value = null
    await refreshAntiforgery()
  }

  /** The session ended elsewhere (a 401): forget the user without asking the server. */
  function sessionEnded(): void {
    user.value = null
  }

  return { user, setupRequired, degraded, loaded, authenticated, load, login, logout, sessionEnded }
})
