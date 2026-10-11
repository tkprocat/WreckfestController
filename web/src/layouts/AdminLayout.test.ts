import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { mount, type VueWrapper } from '@vue/test-utils'

const hub = vi.hoisted(() => ({ status: null as null | import('vue').Ref<string> }))
vi.mock('@/realtime/hub', async () => {
  const { ref } = await import('vue')
  hub.status = ref('connected')
  return { hubStatus: hub.status }
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

beforeEach(() => {
  vi.useFakeTimers()
  hub.status!.value = 'connected'
  window.matchMedia = vi.fn().mockReturnValue({ matches: true, addEventListener: vi.fn(), removeEventListener: vi.fn() })
  wrapper = mount(AdminLayout, { attachTo: document.body })
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
})
