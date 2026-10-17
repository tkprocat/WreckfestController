import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { mount, type VueWrapper } from '@vue/test-utils'

const hub = vi.hoisted(() => ({ status: null as null | import('vue').Ref<string> }))
vi.mock('@/realtime/hub', async () => {
  const { ref } = await import('vue')
  hub.status = ref('connected')
  return { hubStatus: hub.status }
})
const auth = vi.hoisted(() => ({ isAdmin: true, managesThisServer: true }))
vi.mock('@/stores/auth', () => ({ useAuthStore: () => auth }))
const managed = vi.hoisted(() => ({ servers: null as null | import('vue').Ref<{ id: string; name: string; link: string; current: boolean }[]> }))
vi.mock('@/composables/useManagedServers', async () => {
  const { ref } = await import('vue')
  managed.servers = ref([])
  return { useManagedServers: () => ({ servers: managed.servers, load: () => Promise.resolve() }) }
})
vi.mock('vue-router', () => ({
  useRoute: () => ({ name: 'admin-dashboard' }),
  useRouter: () => ({ afterEach: () => () => undefined, resolve: () => ({ href: '/admin' }) }),
  RouterLink: defineComponent({ setup: (_, { slots }) => () => h('a', slots.default?.()) }),
  RouterView: defineComponent({ setup: () => () => h('div', 'page') }),
}))

const { default: AdminLayout } = await import('./AdminLayout.vue')

const NOTICE = 'Live updates are disconnected and reconnecting.'
let wrapper: VueWrapper | undefined

function mountLayout() {
  wrapper = mount(AdminLayout, { attachTo: document.body })
}

beforeEach(() => {
  vi.useFakeTimers()
  hub.status!.value = 'connected'
  auth.isAdmin = true
  auth.managesThisServer = true
  managed.servers!.value = []
  window.matchMedia = vi.fn().mockReturnValue({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() })
  mountLayout()
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  vi.useRealTimers()
  document.body.innerHTML = ''
})

describe('AdminLayout', () => {
  // #231: a page showing a frozen console or roster says so.
  it('says when live updates have been lost for a few seconds, and stops once they are back', async () => {
    hub.status!.value = 'reconnecting'
    await vi.advanceTimersByTimeAsync(4_000)
    expect(wrapper!.text()).toContain(NOTICE)
    // Announced once, politely: NAlert's own role="alert" would nest an assertive region.
    expect(wrapper!.find('[role="status"]').text()).toContain(NOTICE)
    expect(wrapper!.find('[role="alert"]').exists()).toBe(false)

    hub.status!.value = 'connected'
    await nextTick()
    expect(wrapper!.text()).not.toContain(NOTICE)
  })

  it('says nothing about a blip that heals at once', async () => {
    hub.status!.value = 'reconnecting'
    await vi.advanceTimersByTimeAsync(1_000)
    hub.status!.value = 'connected'
    await vi.advanceTimersByTimeAsync(10_000)

    expect(wrapper!.text()).not.toContain(NOTICE)
  })

  // The accounts are a global admin's: anyone else does not see the page in the menu.
  it('shows Users to a global admin only', async () => {
    expect(wrapper!.find('.admin-sidebar').text()).toContain('Users')
    wrapper!.unmount()

    auth.isAdmin = false
    mountLayout()
    await nextTick()

    expect(wrapper!.find('.admin-sidebar').text()).not.toContain('Users')
    expect(wrapper!.find('.admin-sidebar').text()).toContain('Profile')
  })

  it('offers the other servers the user manages, and nothing when there is only this one', async () => {
    expect(wrapper!.find('.server-picker').exists()).toBe(false)

    managed.servers!.value = [
      { id: 'vanilla', name: 'Vanilla', link: '/vanilla/', current: true },
      { id: 'tvtp2', name: 'TVTP2', link: '/tvtp2/', current: false },
    ]
    await nextTick()

    expect(wrapper!.find('.admin-sidebar .server-picker').text()).toContain('Vanilla')
  })

  it('shows someone who does not manage this server only their profile', async () => {
    wrapper!.unmount()
    auth.isAdmin = false
    auth.managesThisServer = false
    mountLayout()
    await nextTick()

    const menu = wrapper!.find('.admin-sidebar nav').text()
    expect(menu).toContain('Profile')
    expect(menu).not.toContain('Dashboard')
    expect(menu).not.toContain('Users')
  })
})
