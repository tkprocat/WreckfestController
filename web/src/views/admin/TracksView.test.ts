import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import TracksView from './TracksView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const tag = (slug: string, name: string) => ({ id: slug.length, name, slug, color: null })
const variant = (id: number, trackId: number, variantId: string, name: string, extra: Record<string, unknown> = {}) => ({
  id,
  variantId,
  name,
  gameMode: 'Racing',
  trackId,
  trackKey: `t${trackId}`,
  trackName: trackId === 1 ? 'Fields' : 'Crash Arena',
  allowedForVoting: true,
  isBuiltIn: true,
  isHidden: false,
  version: 1,
  tags: [] as ReturnType<typeof tag>[],
  ...extra,
})
const track = (id: number, name: string, variants: ReturnType<typeof variant>[], extra: Record<string, unknown> = {}) => ({
  id,
  key: `t${id}`,
  name,
  origin: 'BaseGame',
  dlcName: null,
  mod: null,
  isBuiltIn: true,
  isHidden: false,
  version: 1,
  weather: ['clear'],
  variants,
  ...extra,
})

const fields = () =>
  track(1, 'Fields', [variant(11, 1, 'loop', 'Loop', { tags: [tag('short', 'Short')] }), variant(12, 1, 'loop_rev', 'Loop Reverse', { isHidden: true })])
const arena = () =>
  track(2, 'Crash Arena', [variant(21, 2, 'bowl', 'Bowl', { gameMode: 'Derby', isBuiltIn: false })], { isBuiltIn: false, origin: 'Custom', weather: ['clear', 'rain'] })
const hiddenTrack = () => track(3, 'Old Quarry', [variant(31, 3, 'quarry', 'Quarry')], { isHidden: true })

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

async function mountPage() {
  wrapper = mount(
    defineComponent({ render: () => h(NMessageProvider, () => h(NDialogProvider, () => h(TracksView))) }),
    { attachTo: document.body },
  )
  await flushPromises()
  return wrapper
}

const body = () => new DOMWrapper(document.body)
const labelled = (label: string) => body().find(`[aria-label="${label}"]`)
const trackNames = () => wrapper!.findAll('tbody tr').map((r) => r.text())

async function expand(name: string) {
  const row = wrapper!.findAll('tr').find((r) => r.text().includes(name))!
  await row.find('.n-data-table-expand-trigger').trigger('click')
  await flushPromises()
}

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
  api.GET.mockResolvedValue(answer([arena(), fields(), hiddenTrack()]))
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('TracksView', () => {
  it('asks for hidden tracks too, and shows them only when asked', async () => {
    await mountPage()

    expect(api.GET).toHaveBeenCalledWith('/api/catalogue/tracks', { params: { query: { includeHidden: true } } })
    expect(wrapper!.text()).toContain('Fields')
    expect(wrapper!.text()).not.toContain('Old Quarry')

    await body().find('.n-checkbox').trigger('click')
    expect(wrapper!.text()).toContain('Old Quarry')
  })

  it('finds a track by the name of one of its variants', async () => {
    await mountPage()

    await labelled('Search tracks').setValue('bowl')

    expect(trackNames().join()).toContain('Crash Arena')
    expect(trackNames().join()).not.toContain('Fields')
  })

  it('lists a track\'s variants, leaving hidden ones out', async () => {
    await mountPage()

    await expand('Fields')

    expect(wrapper!.text()).toContain('Loop')
    expect(wrapper!.text()).toContain('Short')
    expect(wrapper!.text()).not.toContain('Loop Reverse')
  })

  it('hides a track and shows the server\'s answer', async () => {
    api.POST.mockResolvedValue(answer({ ...fields(), isHidden: true, version: 2 }))
    await mountPage()

    await labelled('Hide Fields').trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/catalogue/tracks/{id}/hide', { params: { path: { id: 1 } } })
    expect(wrapper!.findAll('tbody tr').some((r) => r.text().includes('Fields'))).toBe(false)
    expect(body().text()).toContain('"Fields" hidden.')
  })

  it('turns voting off for a variant', async () => {
    api.PUT.mockResolvedValue(answer({ ...fields().variants[0], allowedForVoting: false, version: 2 }))
    await mountPage()
    await expand('Fields')

    await labelled('Players can vote for Fields - Loop').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/catalogue/variants/{id}/voting', { params: { path: { id: 11 } }, body: { allowed: false } })
    expect(labelled('Players can vote for Fields - Loop').attributes('aria-checked')).toBe('false')
  })

  it('asks before resetting a built-in track', async () => {
    api.POST.mockResolvedValue(answer(fields()))
    await mountPage()

    await labelled('Reset Fields').trigger('click')
    await flushPromises()
    expect(api.POST).not.toHaveBeenCalled()
    expect(body().find('.n-dialog').text()).toContain('Its variants are left alone.')

    await body().find('.n-dialog').findAll('button').find((b) => b.text() === 'Reset')!.trigger('click')
    await flushPromises()
    expect(api.POST).toHaveBeenCalledWith('/api/catalogue/tracks/{id}/reset', { params: { path: { id: 1 } } })
  })

  // Built-ins can only be hidden; an added track can be deleted, unless a collection uses it.
  it('says why a delete was refused', async () => {
    api.DELETE.mockResolvedValue(refused({ title: 'Collection "Evening" uses bowl.', status: 409 }, 409))
    await mountPage()
    expect(labelled('Delete Fields').exists()).toBe(false)

    await labelled('Delete Crash Arena').trigger('click')
    await flushPromises()
    await body().find('.n-dialog').findAll('button').find((b) => b.text() === 'Delete')!.trigger('click')
    await flushPromises()

    expect(body().text()).toContain('Collection "Evening" uses bowl.')
    expect(wrapper!.text()).toContain('Crash Arena')
  })

  it('filters by game mode and weather', async () => {
    await mountPage()
    const vm = wrapper!.findComponent(TracksView).vm as unknown as { mode: string | null; weather: string | null }

    vm.mode = 'Derby'
    await flushPromises()
    expect(trackNames().join()).not.toContain('Fields')

    vm.mode = null
    vm.weather = 'rain'
    await flushPromises()
    expect(trackNames().join()).toContain('Crash Arena')
    expect(trackNames().join()).not.toContain('Fields')
  })
})
