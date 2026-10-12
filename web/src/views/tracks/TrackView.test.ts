import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createMemoryHistory, createRouter, type Router } from 'vue-router'
import TrackView from './TrackView.vue'
import TrackListView from './TrackListView.vue'
import TrackLink from '@/components/TrackLink.vue'
import { estimateLabel, lengthLabel, originLabel } from './trackText'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const missing = (status: number) => ({ data: undefined, error: {}, response: new Response(null, { status }) })

const images = (small: string | null, large: string | null, layout: string | null = null) => ({ small, large, layout })
const variant = (variantId: string, name: string, extra: Record<string, unknown> = {}) => ({
  variantId,
  name,
  gameMode: 'Racing',
  trackLengthM: 1234,
  images: images('aa', 'bb', 'cc'),
  raceCount: 0,
  ...extra,
})
const track = {
  key: 'madman_stadium',
  name: 'Madman Stadium',
  origin: 'BaseGame',
  modName: null,
  workshopId: null,
  images: images('aa', 'bb'),
  variants: [variant('bigstadium_demolition_arena', 'Demolition Arena', { gameMode: 'Derby', trackLengthM: null }), variant('bigstadium_figure_8', 'Figure 8')],
}
const detail = (extra: Record<string, unknown> = {}) => ({
  variantId: 'bigstadium_figure_8',
  raceCount: 12,
  typicalLapMs: 31_500,
  typicalLapFinishes: 40,
  recentRaces: [],
  fastestLaps: [{ name: 'Bob', vehicleName: 'Roadslayer', lapMs: 28_123, endedAt: '2026-10-10T18:00:00Z' }],
  mostWins: [{ name: 'Alice', wins: 4, races: 9 }],
  cups: [{ name: 'Figure 8 Friday', isActive: false, nextOccurrence: '2026-10-16T18:00:00Z', repeat: 'Every week' }],
  ...extra,
})

let wrapper: VueWrapper | undefined
let router: Router

async function open(path: string) {
  router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/tracks', name: 'tracks', component: TrackListView },
      { path: '/tracks/:track/:variant?', name: 'track', component: TrackView },
    ],
  })
  await router.push(path)
  await router.isReady()
  wrapper = mount({ template: '<RouterView />' }, { global: { plugins: [router] }, attachTo: document.body })
  await flushPromises()
}

function respond(routes: Record<string, unknown>) {
  api.GET.mockImplementation(async (path: string, options?: { params?: { path?: Record<string, string> } }) => {
    const key = `${path} ${JSON.stringify(options?.params?.path ?? {})}`
    return key in routes ? routes[key] : missing(404)
  })
}

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('TrackView', () => {
  it('shows the first layout when the address names none, with its records and an estimate', async () => {
    respond({
      '/api/public/tracks/{key} {"key":"madman_stadium"}': answer(track),
      '/api/public/variants/{variantId} {"variantId":"bigstadium_demolition_arena"}': answer(detail({ variantId: 'bigstadium_demolition_arena' })),
    })

    await open('/tracks/madman_stadium')

    expect(wrapper!.find('h1').text()).toBe('Madman Stadium')
    expect(wrapper!.find('h2').text()).toBe('Demolition Arena')
    expect(wrapper!.find('[aria-current="page"]').text()).toBe('Demolition Arena')
    expect(wrapper!.text()).toContain('Bob')
    expect(wrapper!.text()).toContain('28.123')
    expect(wrapper!.text()).toContain('4 wins')
    expect(wrapper!.text()).toContain('Figure 8 Friday')
    // 5 laps of 31.5 s.
    expect(wrapper!.text()).toContain('about 2:38')
  })

  it('shows the layout the address names, ignoring case, with its outline over the picture', async () => {
    respond({
      '/api/public/tracks/{key} {"key":"madman_stadium"}': answer(track),
      '/api/public/variants/{variantId} {"variantId":"bigstadium_figure_8"}': answer(detail()),
    })

    await open('/tracks/madman_stadium/BIGSTADIUM_FIGURE_8')

    expect(wrapper!.find('h2').text()).toBe('Figure 8')
    const sources = wrapper!.findAll('.picture img').map((i) => i.attributes('src'))
    expect(sources).toEqual(['/api/images/variants/bigstadium_figure_8/large?v=bb', '/api/images/variants/bigstadium_figure_8/layout?v=cc'])
    expect(wrapper!.text()).toContain('1.23 km')
  })

  it('follows a layout link to its own address and loads it', async () => {
    respond({
      '/api/public/tracks/{key} {"key":"madman_stadium"}': answer(track),
      '/api/public/variants/{variantId} {"variantId":"bigstadium_demolition_arena"}': answer(detail({ variantId: 'bigstadium_demolition_arena' })),
      '/api/public/variants/{variantId} {"variantId":"bigstadium_figure_8"}': answer(detail({ raceCount: 77 })),
    })
    await open('/tracks/madman_stadium')

    await wrapper!.findAll('.layouts a').find((a) => a.text() === 'Figure 8')!.trigger('click')
    await flushPromises()

    expect(router.currentRoute.value.fullPath).toBe('/tracks/madman_stadium/bigstadium_figure_8')
    expect(wrapper!.find('h2').text()).toBe('Figure 8')
    expect(wrapper!.text()).toContain('77')
  })

  it('shows only the latest track when an earlier answer comes back last', async () => {
    let releaseFirst: (value: unknown) => void = () => {}
    const first = new Promise((resolve) => (releaseFirst = resolve))
    const other = { ...track, key: 'other', name: 'Other Track' }
    api.GET.mockImplementation(async (path: string, options?: { params?: { path?: Record<string, string> } }) => {
      if (path === '/api/public/tracks/{key}') return options?.params?.path?.key === 'madman_stadium' ? first : answer(other)
      return answer(detail())
    })
    await open('/tracks/madman_stadium')

    await router.push('/tracks/other')
    await flushPromises()
    releaseFirst(answer(track))
    await flushPromises()

    expect(wrapper!.find('h1').text()).toBe('Other Track')
  })

  it('says when there is no such track', async () => {
    respond({})

    await open('/tracks/nowhere')

    expect(wrapper!.text()).toContain('There is no track at this address.')
  })

  it('says when the race history could not be loaded, and keeps the track', async () => {
    respond({
      '/api/public/tracks/{key} {"key":"madman_stadium"}': answer(track),
      '/api/public/variants/{variantId} {"variantId":"bigstadium_demolition_arena"}': missing(429),
    })

    await open('/tracks/madman_stadium')

    expect(wrapper!.find('h1').text()).toBe('Madman Stadium')
    expect(wrapper!.text()).toContain('Too many requests')
  })
})

describe('TrackListView', () => {
  const list = [
    { ...track, variants: track.variants },
    { key: 'asan', name: 'Asan Circuit', origin: 'Workshop', modName: 'The Very TrackPack 2', images: images(null, null), variants: [variant('asan_main', 'Main')] },
  ]

  it('lists the tracks with their pictures and links to each layout', async () => {
    respond({ '/api/public/tracks {}': answer(list) })

    await open('/tracks')

    expect(wrapper!.findAll('h2').map((h) => h.text())).toEqual(['Madman Stadium', 'Asan Circuit'])
    expect(wrapper!.find('.picture img').attributes('src')).toBe('/api/images/tracks/madman_stadium/small?v=aa')
    expect(wrapper!.text()).toContain('Workshop · The Very TrackPack 2')
    const layout = wrapper!.findAll('.layouts a').find((a) => a.text() === 'Figure 8')!
    expect(layout.attributes('href')).toBe('/tracks/madman_stadium/bigstadium_figure_8')
  })

  it('searches track and layout names', async () => {
    respond({ '/api/public/tracks {}': answer(list) })
    await open('/tracks')

    await wrapper!.find('input').setValue('main')

    expect(wrapper!.findAll('h2').map((h) => h.text())).toEqual(['Asan Circuit'])
    expect(wrapper!.text()).toContain('1 track')
  })
})

describe('TrackLink', () => {
  it('links a known track, and leaves anything else as text', async () => {
    router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/tracks/:track/:variant?', name: 'track', component: TrackView }] })
    const linked = mount(TrackLink, { props: { track: { id: 'fields14', name: 'Fields - Long', trackKey: 'fields' } }, global: { plugins: [router] } })
    const plain = mount(TrackLink, { props: { track: { id: 'x', name: 'Mystery', trackKey: null } }, global: { plugins: [router] } })

    expect(linked.find('a').attributes('href')).toBe('/tracks/fields/fields14')
    expect(plain.find('a').exists()).toBe(false)
    expect(plain.text()).toBe('Mystery')
  })
})

describe('track text', () => {
  it('formats origin, length and an estimate', () => {
    expect(originLabel({ origin: 'Workshop', modName: 'Pack' })).toBe('Workshop · Pack')
    expect(originLabel({ origin: 'BaseGame', modName: null })).toBe('Base game')
    expect(lengthLabel(3912.5)).toBe('3.91 km')
    expect(lengthLabel(null)).toBe('')
    expect(estimateLabel(92_000, 5)).toBe('about 7:40')
    expect(estimateLabel(92_000, 50)).toBe('about 1:16:40')
    expect(estimateLabel(null, 5)).toBe('')
    expect(estimateLabel(92_000, 0)).toBe('')
  })
})
