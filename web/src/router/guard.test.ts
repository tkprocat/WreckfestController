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
  auth.authenticated = true
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
})
