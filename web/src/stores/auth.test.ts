import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { canManage, useAuthStore, type User } from './auth'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn() }))
const refreshAntiforgery = vi.hoisted(() => vi.fn())
const restartHub = vi.hoisted(() => vi.fn())

vi.mock('@/api/client', () => ({ portal: api, refreshAntiforgery }))
vi.mock('@/realtime/hub', () => ({ restartHub }))

const admin = { id: '1', userName: 'admin', isAdmin: true, servers: [] as string[] }
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

describe('canManage', () => {
  const someone = (overrides: Partial<User>) => ({ ...admin, isAdmin: false, ...overrides }) as User

  it('lets a global admin manage every server', () => {
    expect(canManage(someone({ isAdmin: true }), 'tvtp2')).toBe(true)
  })

  it('lets a user manage the servers granted them, in any spelling, and no others', () => {
    const racer = someone({ servers: ['TVTP2'] })
    expect(canManage(racer, 'tvtp2')).toBe(true)
    expect(canManage(racer, 'vanilla')).toBe(false)
  })

  it('lets nobody signed out manage anything', () => {
    expect(canManage(null, 'tvtp2')).toBe(false)
  })
})

describe('load after a change elsewhere', () => {
  // The portal's answer says someone else, or nobody, is signed in now: settle as for a
  // sign-out, so the hub stops sending this tab the old user's admin messages.
  it('regroups the hub when the signed-in user is no longer the one it was', async () => {
    // As in the app: the first answer comes before anything else, and regroups nothing.
    const auth = useAuthStore()
    api.GET.mockResolvedValueOnce(answer(200, { authenticated: true, user: admin, setupRequired: false }))
    await auth.load()
    expect(restartHub).not.toHaveBeenCalled()
    api.GET.mockResolvedValueOnce(answer(200, { authenticated: false, user: null, setupRequired: false }))

    expect(await auth.load()).toBe(true)

    expect(auth.user).toBeNull()
    expect(restartHub).toHaveBeenCalledOnce()
  })

  it('leaves the hub alone when the same user is still signed in', async () => {
    const auth = useAuthStore()
    api.GET.mockResolvedValue(answer(200, { authenticated: true, user: admin, setupRequired: false }))
    await auth.load()

    await auth.load()

    expect(restartHub).not.toHaveBeenCalled()
  })
})

describe('load while the session changes', () => {
  it('asks once for several callers at the same time', async () => {
    const auth = useAuthStore()
    api.GET.mockResolvedValue(answer(200, { authenticated: false, user: null, setupRequired: false }))

    await Promise.all([auth.load(), auth.load(), auth.load()])

    expect(api.GET).toHaveBeenCalledOnce()
  })

  // A state request sent before a sign-in answers after it: its "nobody" is older news.
  it('never lets an answer from before a sign-in undo it', async () => {
    const auth = useAuthStore()
    let answerLater: (value: unknown) => void = () => undefined
    api.GET.mockReturnValueOnce(new Promise((resolve) => (answerLater = resolve)))
    const stale = auth.load()
    api.POST.mockResolvedValueOnce(answer(200, admin))
    expect(await auth.login('admin', 'password', false)).toBeNull()

    answerLater(answer(200, { authenticated: false, user: null, setupRequired: false }))
    await stale

    expect(auth.user).toEqual(admin)
  })
})
