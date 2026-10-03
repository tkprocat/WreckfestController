import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import LoginView from './LoginView.vue'

const auth = vi.hoisted(() => ({
  unreachable: false,
  loaded: true,
  degraded: false,
  setupRequired: false,
  login: vi.fn(),
  load: vi.fn(),
}))
const route = vi.hoisted(() => ({ query: { redirect: '/admin/tracks' } }))
const router = vi.hoisted(() => ({ replace: vi.fn() }))
vi.mock('@/stores/auth', async () => {
  const { reactive } = await import('vue')
  return { useAuthStore: () => reactive(auth) }
})
vi.mock('vue-router', () => ({ useRoute: () => route, useRouter: () => router }))

let wrapper: VueWrapper | undefined
beforeEach(() => {
  Object.assign(auth, { unreachable: false, loaded: true, degraded: false, setupRequired: false })
  auth.login.mockReset()
  auth.load.mockReset()
  router.replace.mockReset()
})
afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
})

describe('LoginView', () => {
  it('keeps setup, database, and unreachable guidance visible', () => {
    auth.setupRequired = true
    wrapper = mount(LoginView)
    expect(wrapper.text()).toContain('Create the first admin account')
    wrapper.unmount()

    auth.setupRequired = false
    auth.degraded = true
    wrapper = mount(LoginView)
    expect(wrapper.text()).toContain('database is unavailable')
    wrapper.unmount()

    auth.degraded = false
    auth.unreachable = true
    auth.loaded = false
    wrapper = mount(LoginView)
    expect(wrapper.text()).toContain('controller cannot be reached')
    expect(wrapper.text()).toContain('Try again')
  })

  it.each([
    ['invalid', 'That login or password is not right.'],
    ['locked', 'This account is locked for now.'],
    ['rate-limited', 'Too many attempts.'],
    ['unavailable', 'Signing in is not possible right now.'],
  ])('shows %s feedback after a failed sign-in', async (failure, message) => {
    auth.login.mockResolvedValue(failure)
    wrapper = mount(LoginView)
    await wrapper.find('input[name="login"]').setValue('admin')
    await wrapper.find('input[name="password"]').setValue('secret')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain(message)
    expect(router.replace).not.toHaveBeenCalled()
  })

  it('preserves the requested redirect after a successful sign-in', async () => {
    auth.login.mockResolvedValue(null)
    wrapper = mount(LoginView)
    await wrapper.find('input[name="login"]').setValue('admin')
    await wrapper.find('input[name="password"]').setValue('secret')
    await wrapper.find('form').trigger('submit')
    await flushPromises()
    expect(router.replace).toHaveBeenCalledWith('/admin/tracks')
  })
})
