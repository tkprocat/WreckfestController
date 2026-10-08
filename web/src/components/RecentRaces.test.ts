import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { mount } from '@vue/test-utils'
import RecentRaces from './RecentRaces.vue'
import type { PublicRace } from '@/composables/usePublicRaces'

const state = vi.hoisted(() => ({ races: null as unknown, error: null as string | null, loading: false }))

vi.mock('@/composables/usePublicRaces', () => ({
  usePublicRaces: () => ({ races: ref(state.races), error: ref(state.error), loading: ref(state.loading), reload: vi.fn() }),
}))

const entry = (position: number | null, name: string, extra: object = {}) => ({
  position,
  name,
  isBot: false,
  vehicleName: 'Roadslayer',
  outcome: 'Finished',
  timeMs: 180_000,
  bestLapMs: 59_000,
  ...extra,
})

const race: PublicRace = {
  id: 7,
  startedAt: null,
  endedAt: new Date(Date.now() - 5 * 60_000).toISOString(),
  track: { id: 'fields14', name: 'Fields - Long' },
  laps: 3,
  cupName: 'Friday Cup',
  entries: [
    entry(1, 'Winner'),
    entry(2, 'Speedy AI', { isBot: true, outcome: 'Projected', timeMs: 185_120 }),
    entry(3, 'Third'),
    entry(4, 'Fourth'),
    entry(null, 'Crashed', { outcome: 'DidNotFinish', timeMs: null, bestLapMs: null }),
  ],
} as unknown as PublicRace

beforeEach(() => {
  state.races = [race]
  state.error = null
  state.loading = false
})

describe('RecentRaces', () => {
  it('shows the track, when, laps and cup, and the top three with bots in their places', () => {
    const wrapper = mount(RecentRaces)

    expect(wrapper.find('h3').text()).toBe('Fields - Long')
    const meta = wrapper.find('.race-meta').text()
    expect(meta).toContain('5 minutes ago')
    expect(meta).toContain('3 laps')
    expect(meta).toContain('Friday Cup')

    const podium = wrapper.findAll('.podium li')
    expect(podium.map((li) => li.find('strong').text())).toEqual(['Winner', 'Speedy AI', 'Third'])
    expect(podium[1]!.text()).toContain('Bot')
    expect(podium[1]!.text()).toContain('~3:05.120')
  })

  it('lists every car, unplaced and DNF included, under the full results', () => {
    const wrapper = mount(RecentRaces)

    const details = wrapper.find('details')
    expect(details.find('summary').text()).toBe('Full results (5 cars)')
    const rows = details.findAll('tbody tr')
    expect(rows.map((r) => r.find('th').text().replace('Bot', '').trim())).toEqual([
      'Winner',
      'Speedy AI',
      'Third',
      'Fourth',
      'Crashed',
    ])
    expect(rows[4]!.text()).toContain('DNF')
    expect(rows[4]!.find('td').text()).toBe('–')
  })

  it('leaves out the cup and laps when there are none', () => {
    state.races = [{ ...race, cupName: null, laps: 0 }]

    const meta = mount(RecentRaces).find('.race-meta').text()

    expect(meta).not.toContain('Friday Cup')
    expect(meta).not.toContain('lap')
  })

  it('says when no races are recorded yet', () => {
    state.races = []
    expect(mount(RecentRaces).text()).toContain('No races recorded yet')
  })

  it('shows the error when nothing has loaded, and marks a kept list as last known', () => {
    state.races = null
    state.error = 'Recent races could not be loaded.'
    expect(mount(RecentRaces).text()).toContain('Recent races could not be loaded.')

    state.races = [race]
    const text = mount(RecentRaces).text()
    expect(text).toContain('Last known results')
    expect(text).toContain('Fields - Long')
  })
})
