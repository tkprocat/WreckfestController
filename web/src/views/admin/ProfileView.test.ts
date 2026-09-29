import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NMessageProvider } from 'naive-ui'
import ProfileView from './ProfileView.vue'

const api = vi.hoisted(() => ({ PUT: vi.fn(), POST: vi.fn() }))
const auth = vi.hoisted(() => ({ user: null as unknown }))
vi.mock('@/api/client', () => ({ api }))
vi.mock('@/stores/auth', async () => {
  const { reactive } = await import('vue')
  const store = reactive(auth)
  return { useAuthStore: () => store }
})

const me = {
  id: '1',
  userName: 'admin',
  email: 'admin@example.com',
  displayName: null,
  timeZone: 'Europe/Copenhagen',
  isLockedOut: false,
  lockoutEnd: null,
}

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

function mountPage() {
  wrapper = mount(defineComponent({ render: () => h(NMessageProvider, () => h(ProfileView)) }), { attachTo: document.body })
  return wrapper
}

const input = (label: string) => wrapper!.find(`input[aria-label="${label}"]`)
const button = (label: string) => wrapper!.findAll('button').find((b) => b.text().trim() === label)!

beforeEach(async () => {
  api.PUT.mockReset()
  api.POST.mockReset()
  const { useAuthStore } = await import('@/stores/auth')
  ;(useAuthStore() as { user: unknown }).user = { ...me }
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('ProfileView', () => {
  it('saves the profile and updates who is signed in', async () => {
    api.PUT.mockResolvedValue(answer({ ...me, displayName: 'The Admin' }))
    mountPage()
    await flushPromises()

    await input('Display name').setValue('The Admin')
    await button('Save').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/auth/me', {
      body: { email: 'admin@example.com', displayName: 'The Admin', timeZone: 'Europe/Copenhagen' },
    })
    const { useAuthStore } = await import('@/stores/auth')
    expect((useAuthStore() as { user: { displayName: string } }).user.displayName).toBe('The Admin')
  })

  // A typo in the new password must not lock its owner out.
  it('needs the new password twice, the same', async () => {
    mountPage()
    await flushPromises()

    await input('Current password').setValue('old-password')
    await input('New password').setValue('new-password-1')
    await input('New password again').setValue('new-password-2')

    expect(wrapper!.text()).toContain('The two new passwords are not the same.')
    expect(button('Change password').attributes('disabled')).toBeDefined()
  })

  it('changes the password and clears the form', async () => {
    api.POST.mockResolvedValue(answer(undefined, 204))
    mountPage()
    await flushPromises()

    await input('Current password').setValue('old-password')
    await input('New password').setValue('new-password-1')
    await input('New password again').setValue('new-password-1')
    await button('Change password').trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/auth/me/password', {
      body: { currentPassword: 'old-password', newPassword: 'new-password-1' },
    })
    expect((input('Current password').element as HTMLInputElement).value).toBe('')
    expect(document.body.textContent).toContain('Password changed.')
  })

  it('shows a wrong current password under its field', async () => {
    api.POST.mockResolvedValue(refused({ title: 'Invalid', errors: { currentPassword: ['The current password is not right.'] } }, 400))
    mountPage()
    await flushPromises()

    await input('Current password').setValue('wrong')
    await input('New password').setValue('new-password-1')
    await input('New password again').setValue('new-password-1')
    await button('Change password').trigger('click')
    await flushPromises()

    expect(wrapper!.text()).toContain('The current password is not right.')
  })

  // An API key is signed in but is not an account.
  it('explains when the sign-in has no profile', async () => {
    const { useAuthStore } = await import('@/stores/auth')
    ;(useAuthStore() as { user: unknown }).user = null
    mountPage()
    await flushPromises()

    expect(wrapper!.text()).toContain('An API key is not an account.')
  })
})
