import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import StatusBadge from '@/components/StatusBadge.vue'
import UsersView from './UsersView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
const auth = vi.hoisted(() => ({ user: null as unknown }))
vi.mock('@/api/client', () => ({ api }))
vi.mock('@/stores/auth', async () => {
  const { reactive } = await import('vue')
  const store = reactive(auth)
  return { useAuthStore: () => store }
})

const user = (id: string, userName: string, overrides: Record<string, unknown> = {}) => ({
  id,
  userName,
  email: `${userName}@example.com`,
  displayName: null,
  timeZone: 'Europe/Copenhagen',
  isLockedOut: false,
  lockoutEnd: null,
  ...overrides,
})

const me = user('1', 'admin')
const other = user('2', 'bob')

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

function mountPage() {
  wrapper = mount(
    defineComponent({ render: () => h(NMessageProvider, () => h(NDialogProvider, () => h(UsersView))) }),
    { attachTo: document.body },
  )
  return wrapper
}

const body = () => new DOMWrapper(document.body)
const rowOf = (name: string) => wrapper!.findAll('tr').find((row) => row.text().includes(name))!
const menuOption = (action: string) => body().findAll('.n-dropdown-option-body').filter((option) => option.text().trim() === action).slice(-1)[0]!
async function openMenu(row: string) {
  await body().find('[aria-label="More actions for ' + row + '"]').trigger('click')
  await flushPromises()
}
async function menuAction(row: string, action: string) {
  await openMenu(row)
  await menuOption(action).trigger('click')
  await flushPromises()
}
const bodyButton = (label: string) => body().findAll('button').find((b) => b.text().trim() === label)!

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
  auth.user = me
  api.GET.mockResolvedValue(answer([me, other]))
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('UsersView', () => {
  it('shows active and locked accounts with the correct static status tones', async () => {
    api.GET.mockResolvedValue(answer([me, { ...other, isLockedOut: true }]))
    mountPage()
    await flushPromises()
    const badges = wrapper!.findAllComponents(StatusBadge)
    expect(badges.map((badge) => [badge.text(), badge.props('tone')])).toEqual([
      ['Active', 'positive'], ['Locked', 'negative'],
    ])
    expect(badges.every((badge) => badge.attributes('role') === undefined)).toBe(true)
  })
  // The server refuses it too; the page does not offer it.
  it('does not offer to lock or delete your own account', async () => {
    mountPage()
    await flushPromises()

    expect(rowOf('admin').text()).toContain('you')
    await openMenu('admin')
    expect(menuOption('Lock').classes()).toContain('n-dropdown-option-body--disabled')
    expect(menuOption('Delete').classes()).toContain('n-dropdown-option-body--disabled')
    await openMenu('bob')
    expect(menuOption('Delete').classes()).not.toContain('n-dropdown-option-body--disabled')
  })

  it('adds an account with a temporary password', async () => {
    api.POST.mockResolvedValue(answer(user('3', 'carol')))
    mountPage()
    await flushPromises()

    await bodyButton('Add account').trigger('click')
    await flushPromises()
    await body().find('input[aria-label="User name"]').setValue(' carol ')
    await body().find('input[aria-label="Email"]').setValue('carol@example.com')
    await body().find('input[aria-label="Temporary password"]').setValue('temporary-1')
    await bodyButton('Add').trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/users', {
      body: expect.objectContaining({ userName: 'carol', email: 'carol@example.com', password: 'temporary-1' }),
    })
    expect(wrapper!.text()).toContain('carol')
  })

  it('shows a field error in the form', async () => {
    api.POST.mockResolvedValue(refused({ title: 'Invalid', errors: { userName: ['That user name is taken.'] } }, 400))
    mountPage()
    await flushPromises()

    await bodyButton('Add account').trigger('click')
    await flushPromises()
    await body().find('input[aria-label="User name"]').setValue('bob')
    await bodyButton('Add').trigger('click')
    await flushPromises()

    expect(body().text()).toContain('That user name is taken.')
  })

  it('asks before deleting, then deletes', async () => {
    api.DELETE.mockResolvedValue(answer(undefined, 204))
    mountPage()
    await flushPromises()

    await menuAction('bob', 'Delete')
    await flushPromises()
    expect(api.DELETE).not.toHaveBeenCalled()
    expect(body().text()).toContain('Delete bob?')

    await body().findAll('.n-dialog button').find((b) => b.text().trim() === 'Delete')!.trigger('click')
    await flushPromises()

    expect(api.DELETE).toHaveBeenCalledWith('/api/users/{id}', { params: { path: { id: '2' } } })
    expect(wrapper!.text()).not.toContain('bob@example.com')
  })

  it('locks an account and shows it', async () => {
    api.POST.mockResolvedValue(answer({ ...other, isLockedOut: true }))
    mountPage()
    await flushPromises()

    await menuAction('bob', 'Lock')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/users/{id}/lock', { params: { path: { id: '2' } } })
    expect(rowOf('bob').text()).toContain('Locked')
    await openMenu('bob')
    expect(menuOption('Unlock')).toBeDefined()
  })

  // Until the first load answers, a new row would be replaced by that older answer.
  it('offers Add account only once the list has loaded', async () => {
    let finish!: (value: unknown) => void
    api.GET.mockReturnValue(new Promise((resolve) => (finish = resolve)))
    mountPage()
    await flushPromises()

    expect(bodyButton('Add account').attributes('disabled')).toBeDefined()

    finish(answer([me, other]))
    await flushPromises()
    expect(bodyButton('Add account').attributes('disabled')).toBeUndefined()
  })

  it('shows the table loading, not empty, until the list answers', async () => {
    api.GET.mockReturnValue(new Promise(() => {}))
    mountPage()
    await flushPromises()

    expect(wrapper!.text()).not.toContain('No accounts yet.')
    expect(wrapper!.find('.n-data-table .n-base-loading').exists()).toBe(true)
  })

  // A dialog is named for screen readers, and its time zone input where focus lands.
  it('names the account dialog and its time zone input', async () => {
    mountPage()
    await flushPromises()

    await bodyButton('Add account').trigger('click')
    await flushPromises()

    expect(body().find('[role="dialog"][aria-label="Add account"]').exists()).toBe(true)
    expect(body().find('input[aria-label="Time zone"]').exists()).toBe(true)
  })

  it('resets a password', async () => {
    api.POST.mockResolvedValue(answer(undefined, 204))
    mountPage()
    await flushPromises()

    await menuAction('bob', 'Reset password')
    await flushPromises()
    await body().find('input[aria-label="New temporary password"]').setValue('another-temp-1')
    await body().findAll('.n-modal button').find((b) => b.text().trim() === 'Reset password')!.trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/users/{id}/password', {
      params: { path: { id: '2' } },
      body: { newPassword: 'another-temp-1' },
    })
    expect(body().text()).toContain('Password reset for bob.')
  })
})
