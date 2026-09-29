import { beforeEach, describe, expect, it, vi } from 'vitest'

const auth = vi.hoisted(() => ({ loaded: true, authenticated: true, load: vi.fn() }))
vi.mock('@/stores/auth', () => ({ useAuthStore: () => auth }))

// Only the guard is under test: the pages are stand-ins, so navigating does not load them.
const page = { render: () => null }
for (const view of [
  '@/views/HomeView.vue',
  '@/views/LoginView.vue',
  '@/views/NotFoundView.vue',
  '@/layouts/AdminLayout.vue',
  '@/views/admin/DashboardView.vue',
  '@/views/admin/ServerControlView.vue',
  '@/views/admin/SettingsView.vue',
]) {
  vi.doMock(view, () => ({ default: page }))
}

const { router } = await import('./index')

beforeEach(async () => {
  auth.loaded = true
  auth.authenticated = true
  auth.load.mockReset()
  await router.push('/')
})

describe('router guard', () => {
  // Signed in already: the sign-in page has nothing to do, so it goes where it would have.
  it('sends a signed-in user past the sign-in page', async () => {
    await router.push('/login?redirect=/admin/server')

    expect(router.currentRoute.value.fullPath).toBe('/admin/server')
  })

  it('goes to the admin area when nothing sent the user to sign in', async () => {
    await router.push('/login')

    expect(router.currentRoute.value.fullPath).toBe('/admin')
  })

  it('shows the sign-in page to someone signed out', async () => {
    auth.authenticated = false

    await router.push('/login?redirect=/admin/server')

    expect(router.currentRoute.value.name).toBe('login')
  })

  // Routes match regardless of case: /LOGIN is the sign-in page too, and must not bounce
  // a signed-in user back to it.
  it('sends a signed-in user past the sign-in page in any spelling', async () => {
    await router.push('/LOGIN?redirect=/Login')

    expect(router.currentRoute.value.fullPath).toBe('/admin')
  })

  // The controller cannot be reached: nobody counts as signed in, the sign-in page shows,
  // and the admin area sends there - no loop, and the next navigation asks again.
  it('shows the sign-in page when the controller cannot be reached', async () => {
    auth.loaded = false
    auth.authenticated = false
    auth.load.mockResolvedValue(false)

    await router.push('/admin/server')

    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/admin/server')
    expect(auth.load).toHaveBeenCalled()
  })
})
