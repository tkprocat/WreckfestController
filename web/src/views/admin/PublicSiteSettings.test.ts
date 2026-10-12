import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NMessageProvider } from 'naive-ui'
import PublicSiteSettings from './PublicSiteSettings.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), PUT: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

function mountCard() {
  wrapper = mount(defineComponent({ render: () => h(NMessageProvider, () => h(PublicSiteSettings)) }), { attachTo: document.body })
  return wrapper
}

const button = (label: string) => wrapper!.findAll('button').find((b) => b.text().trim() === label)!
const count = () => wrapper!.find('input[aria-label="Items in recent races"]')

async function setCount(value: number) {
  await count().setValue(String(value))
  await count().trigger('blur')
}

beforeEach(() => {
  api.GET.mockReset()
  api.PUT.mockReset()
  api.GET.mockResolvedValue(answer({ feedItemCount: 10, version: 2 }))
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('PublicSiteSettings', () => {
  it('saves the item count with the loaded version as If-Match', async () => {
    api.PUT.mockResolvedValue(answer({ feedItemCount: 20, version: 3 }))
    mountCard()
    await flushPromises()

    expect((count().element as HTMLInputElement).value).toBe('10')
    expect(button('Save').attributes('disabled')).toBeDefined()
    await setCount(20)
    await button('Save').trigger('click')
    await flushPromises()

    expect(api.GET).toHaveBeenCalledWith('/api/settings/publicSite')
    expect(api.PUT).toHaveBeenCalledWith('/api/settings/publicSite', {
      body: { feedItemCount: 20 },
      headers: { 'If-Match': '"2"' },
    })
    expect(document.body.textContent).toContain('Front page settings saved.')
    expect(button('Save').attributes('disabled')).toBeDefined()
  })

  it('reloads and says so when the setting was changed elsewhere', async () => {
    api.PUT.mockResolvedValue(refused({ feedItemCount: 5, version: 3 }, 409))
    mountCard()
    await flushPromises()

    await setCount(20)
    await button('Save').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('changed elsewhere')
    expect((count().element as HTMLInputElement).value).toBe('5')
  })

  it('shows a field error under the field', async () => {
    api.PUT.mockResolvedValue(
      refused({ title: 'One or more validation errors occurred.', errors: { feedItemCount: ['feedItemCount must be between 1 and 50.'] } }, 400),
    )
    mountCard()
    await flushPromises()

    await setCount(20)
    await button('Save').trigger('click')
    await flushPromises()

    expect(wrapper!.text()).toContain('feedItemCount must be between 1 and 50.')
  })

  it('says when the setting could not be loaded', async () => {
    api.GET.mockResolvedValue(refused({ title: 'Nope' }, 503))
    mountCard()
    await flushPromises()

    expect(wrapper!.text()).toContain('Nope')
  })
})
