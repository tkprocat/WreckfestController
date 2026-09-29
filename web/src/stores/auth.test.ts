import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from './auth'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn() }))
const refreshAntiforgery = vi.hoisted(() => vi.fn())
const restartHub = vi.hoisted(() => vi.fn())

vi.mock('@/api/client', () => ({ api, refreshAntiforgery }))
vi.mock('@/realtime/hub', () => ({ restartHub }))

const admin = { userName: 'admin' }
const answer = (status: number, data?: unknown) => ({ data, response: new Response(null, { status }) })

beforeEach(() => {
  setActivePinia(createPinia())
  vi.resetAllMocks()
  refreshAntiforgery.mockResolvedValue(undefined)
  restartHub.mockResolvedValue(undefined)
})

async function signedIn() {
  const auth = useAuthStore()
  api.POST.mockResolvedValueOnce(answer(200, admin))
  expect(await auth.login('admin', 'password', false)).toBeNull()
  vi.clearAllMocks()
  return auth
}

describe('logout', () => {
  // The session cookie may still be valid: the page must not say it is signed out.
  it.each([400, 503])('keeps the user when the server answers %i', async (status) => {
    const auth = await signedIn()
    api.POST.mockResolvedValueOnce(answer(status))

    expect(await auth.logout()).toBe(false)

    expect(auth.authenticated).toBe(true)
    expect(restartHub).not.toHaveBeenCalled()
  })

  it('forgets the user, then gets a new token and regroups the hub', async () => {
    const auth = await signedIn()
    api.POST.mockResolvedValueOnce(answer(204))

    expect(await auth.logout()).toBe(true)

    expect(auth.authenticated).toBe(false)
    expect(refreshAntiforgery).toHaveBeenCalledTimes(1)
    expect(restartHub).toHaveBeenCalledTimes(1)
  })
})

describe('sessionEnded', () => {
  // The server only assigns hub groups when a connection starts: without a restart, an
  // ended session's socket would keep getting the admin group's messages.
  it('regroups the hub and gets an anonymous token', async () => {
    const auth = await signedIn()

    auth.sessionEnded()
    await vi.waitFor(() => expect(restartHub).toHaveBeenCalledTimes(1))

    expect(auth.authenticated).toBe(false)
    expect(refreshAntiforgery).toHaveBeenCalledTimes(1)
  })

  // A token fetch that hangs must not keep the ended session's socket in the admin group.
  it('regroups the hub without waiting for the token', async () => {
    const auth = await signedIn()
    refreshAntiforgery.mockReturnValueOnce(new Promise(() => undefined))

    auth.sessionEnded()

    await vi.waitFor(() => expect(restartHub).toHaveBeenCalledTimes(1))
  })

  it('does nothing when nobody was signed in', () => {
    useAuthStore().sessionEnded()

    expect(restartHub).not.toHaveBeenCalled()
  })
})

describe('login', () => {
  // Signing in succeeded; a token or hub failure afterwards must not turn it into an error.
  it('still succeeds, and still regroups the hub, when the token refresh fails', async () => {
    const auth = useAuthStore()
    api.POST.mockResolvedValueOnce(answer(200, admin))
    refreshAntiforgery.mockRejectedValueOnce(new Error('offline'))

    expect(await auth.login('admin', 'password', false)).toBeNull()

    expect(auth.authenticated).toBe(true)
    expect(restartHub).toHaveBeenCalledTimes(1)
  })

  it.each([
    [401, 'invalid'],
    [423, 'locked'],
    [429, 'rate-limited'],
    [503, 'unavailable'],
  ])('reports %i as %s', async (status, failure) => {
    api.POST.mockResolvedValueOnce(answer(status))

    expect(await useAuthStore().login('admin', 'wrong', false)).toBe(failure)
  })
})

describe('load', () => {
  // Public pages must still render: load() never throws, and asks again next time.
  it('records an unreachable controller instead of throwing', async () => {
    const auth = useAuthStore()
    api.GET.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    expect(await auth.load()).toBe(false)

    expect(auth.unreachable).toBe(true)
    expect(auth.loaded).toBe(false)
    expect(auth.authenticated).toBe(false)
  })

  it('recovers on the next attempt', async () => {
    const auth = useAuthStore()
    api.GET.mockResolvedValueOnce(answer(500))
    api.GET.mockResolvedValueOnce(answer(200, { user: admin, setupRequired: false, degraded: false }))

    expect(await auth.load()).toBe(false)
    expect(await auth.load()).toBe(true)

    expect(auth.unreachable).toBe(false)
    expect(auth.loaded).toBe(true)
    expect(auth.authenticated).toBe(true)
  })
})
