import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NMessageProvider } from 'naive-ui'
import SettingsView from './SettingsView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), PUT: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const vote = (overrides: Record<string, unknown> = {}) => ({
  mode: 'Voting',
  directCooldownSeconds: 30,
  voteTimeoutSeconds: 30,
  maxLapsAllowed: 10,
  messageDelayMs: 250,
  suppressCommandsDuringRace: false,
  version: 3,
  ...overrides,
})

const variant = (id: number, allowedForVoting: boolean) => ({
  id,
  variantId: `track_${id}`,
  name: `Layout ${id}`,
  gameMode: 'Racing',
  trackId: id,
  trackKey: `track${id}`,
  trackName: `Track ${id}`,
  allowedForVoting,
  isBuiltIn: true,
  isHidden: false,
  version: 1,
  tags: [],
})

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

function serve(settings = vote(), variants = [variant(1, true), variant(2, false)]) {
  api.GET.mockImplementation((path: string) =>
    Promise.resolve(path === '/api/settings/vote' ? answer(settings) : answer(variants)),
  )
}

let wrapper: VueWrapper | undefined

function mountPage() {
  wrapper = mount(defineComponent({ render: () => h(NMessageProvider, () => h(SettingsView)) }), { attachTo: document.body })
  return wrapper
}

const button = (label: string) => wrapper!.findAll('button').find((b) => b.text().trim() === label)!

async function setMaxLaps(value: number) {
  const input = wrapper!.findAll('input').find((i) => i.element.closest('.n-form-item')?.textContent?.includes('Most laps'))!
  await input.setValue(String(value))
  await input.trigger('blur')
}

beforeEach(() => {
  api.GET.mockReset()
  api.PUT.mockReset()
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('SettingsView', () => {
  // Only what changed, against the version it was loaded at: a change made elsewhere
  // to another field is not undone.
  it('saves only the changed fields, with the loaded version as If-Match', async () => {
    serve()
    api.PUT.mockResolvedValue(answer(vote({ maxLapsAllowed: 12, version: 4 })))
    mountPage()
    await flushPromises()

    expect(button('Save').attributes('disabled')).toBeDefined()
    await setMaxLaps(12)
    await button('Save').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/settings/vote', {
      body: { maxLapsAllowed: 12 },
      headers: { 'If-Match': '"3"' },
    })
    expect(document.body.textContent).toContain('Voting settings saved.')
  })

  it('reloads and says so when the settings were changed elsewhere', async () => {
    serve()
    api.PUT.mockResolvedValue(refused(vote({ maxLapsAllowed: 5, version: 4 }), 409))
    mountPage()
    await flushPromises()

    await setMaxLaps(12)
    await button('Save').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('changed elsewhere')
    const laps = wrapper!.findAll('input').find((i) => i.element.closest('.n-form-item')?.textContent?.includes('Most laps'))!
    expect((laps.element as HTMLInputElement).value).toBe('5')
    expect(button('Save').attributes('disabled')).toBeDefined()
  })

  it('shows a field error under the field', async () => {
    serve()
    api.PUT.mockResolvedValue(
      refused({ title: 'One or more validation errors occurred.', errors: { maxLapsAllowed: ['maxLapsAllowed must be between 1 and 999.'] } }, 400),
    )
    mountPage()
    await flushPromises()

    await setMaxLaps(12)
    await button('Save').trigger('click')
    await flushPromises()

    expect(wrapper!.text()).toContain('maxLapsAllowed must be between 1 and 999.')
  })

  it('switches voting for a track, and keeps the old state when that fails', async () => {
    serve()
    api.PUT.mockResolvedValueOnce(answer(variant(2, true)))
    mountPage()
    await flushPromises()
    expect(wrapper!.text()).toContain('1 of 2 votable')

    await wrapper!.find('[aria-label="Votable: Track 2 - Layout 2"]').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/catalogue/variants/{id}/voting', { params: { path: { id: 2 } }, body: { allowed: true } })
    expect(wrapper!.text()).toContain('2 of 2 votable')

    api.PUT.mockResolvedValueOnce(refused({ title: 'This variant was changed elsewhere.' }, 409))
    await wrapper!.find('[aria-label="Votable: Track 1 - Layout 1"]').trigger('click')
    await flushPromises()

    expect(wrapper!.text()).toContain('2 of 2 votable')
    expect(document.body.textContent).toContain('This variant was changed elsewhere.')
  })

  it('filters the tracks by search', async () => {
    serve()
    mountPage()
    await flushPromises()

    await wrapper!.find('input[placeholder="Search tracks and layouts"]').setValue('track 2')

    expect(wrapper!.text()).toContain('Layout 2')
    expect(wrapper!.text()).not.toContain('Layout 1')
  })
})
