import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { mount } from '@vue/test-utils'
import RecentRaces from './RecentRaces.vue'
import type { PublicCupRun, PublicFeedItem, PublicRace } from '@/composables/usePublicFeed'

const state = vi.hoisted(() => ({ items: null as unknown, error: null as string | null, loading: false, reload: vi.fn() }))

vi.mock('@/composables/usePublicFeed', () => ({
  usePublicFeed: () => ({ items: ref(state.items), error: ref(state.error), loading: ref(state.loading), reload: state.reload }),
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
  warmup: false,
  entries: [
    entry(1, 'Winner'),
    entry(2, 'Speedy AI', { isBot: true, outcome: 'Projected', timeMs: 185_120 }),
    entry(3, 'Third'),
    entry(4, 'Fourth'),
    entry(null, 'Crashed', { outcome: 'DidNotFinish', timeMs: null, bestLapMs: null }),
  ],
} as unknown as PublicRace

const asItem = (r: PublicRace): PublicFeedItem => ({ kind: 'Race', race: r, cup: null })

beforeEach(() => {
  state.items = [asItem(race)]
  state.error = null
  state.loading = false
  state.reload.mockReset()
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
    expect(podium[1]!.text()).toContain('3:05.120 est.')
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

  // A title alone reaches neither touch nor keyboard users.
  it('explains est. and DNF in visible text, only when they appear', () => {
    expect(mount(RecentRaces).find('.legend').text()).toBe(
      "est. – a bot's time, estimated by the game when the last player finished.DNF – did not finish.",
    )

    state.items = [asItem({ ...race, entries: [race.entries[0]!] })]
    expect(mount(RecentRaces).find('.legend').exists()).toBe(false)
  })

  it('leaves out the cup and laps when there are none', () => {
    state.items = [asItem({ ...race, cupName: null, laps: 0 })]

    const meta = mount(RecentRaces).find('.race-meta').text()

    expect(meta).not.toContain('Friday Cup')
    expect(meta).not.toContain('lap')
  })

  it('says when no races are recorded yet', () => {
    state.items = []
    expect(mount(RecentRaces).text()).toContain('No races recorded yet')
  })

  it('shows the error with a retry when nothing has loaded', async () => {
    state.items = null
    state.error = 'Recent races could not be loaded.'

    const wrapper = mount(RecentRaces)
    expect(wrapper.text()).toContain('Recent races could not be loaded.')
    expect(wrapper.text()).not.toContain('No races recorded yet')
    await wrapper.find('button').trigger('click')

    expect(state.reload).toHaveBeenCalled()
  })

  // Even when the kept list is empty, the failure must not pass for "no races".
  it('shows a failed refresh next to the list it kept', () => {
    state.error = 'Recent races could not be loaded.'

    const text = mount(RecentRaces).text()
    expect(text).toContain('Last known results')
    expect(text).toContain('Recent races could not be loaded. Showing the last list loaded.')
    expect(text).toContain('Fields - Long')

    state.items = []
    expect(mount(RecentRaces).text()).toContain('Recent races could not be loaded. Showing the last list loaded.')
  })

  describe('a cup run', () => {
    const cup = (extra: Partial<PublicCupRun> = {}): PublicCupRun => ({
      name: 'Friday Cup',
      activatedAt: '2026-10-09T18:00:00Z',
      status: 'Ended',
      startsAt: null,
      endsAt: null,
      standings: [
        { position: 1, name: 'Alice', isBot: false, points: 18 },
        { position: 1, name: 'Bot Ann', isBot: true, points: 18 },
        { position: 3, name: 'Bob', isBot: false, points: 6 },
      ],
      raceCount: 2,
      races: [race, { ...race, id: 6, warmup: true, track: { id: 'speedway2', name: 'Speedway' } }],
      ...extra,
    })
    const asCup = (c: PublicCupRun): PublicFeedItem => ({ kind: 'Cup', race: null, cup: c })

    it('shows the standings on top and its races below, warmup marked', async () => {
      state.items = [asCup(cup()), asItem({ ...race, id: 9 })]

      const wrapper = mount(RecentRaces)
      const items = wrapper.findAll('.race-list > li')
      expect(items).toHaveLength(2)

      const card = items[0]!
      expect(card.find('h3').text()).toBe('Friday Cup')
      expect(card.text()).toContain('FINAL')
      expect(card.find('h4').text()).toBe('Final standings')
      const row = (li: { find: (s: string) => { text: () => string } }) =>
        [li.find('.place').text(), li.find('strong').text(), li.find('.points').text()].join(' ')
      // Bots are hidden until asked for; the others keep their places.
      expect(card.findAll('.standings-list li').map(row)).toEqual(['1 Alice 18 pts', '3 Bob 6 pts'])
      expect(card.findAll('.cup-race-list details')[0]!.findAll('tbody tr')).toHaveLength(4)
      expect(card.find('.bots-switch [role="switch"]').attributes('aria-label')).toBe('Show bots')
      await card.find('.bots-switch [role="switch"]').trigger('click')
      expect(card.findAll('.standings-list li').map(row)).toEqual(['1 Alice 18 pts', '1 Bot Ann 18 pts', '3 Bob 6 pts'])
      expect(card.findAll('.standings-list li')[1]!.classes()).toContain('bot')

      const races = card.findAll('.cup-race-list details')
      expect(races.map((d) => d.find('.race-name').text())).toEqual(['Fields - Long', 'Speedway'])
      expect(races[0]!.find('summary').text()).toContain('won by Winner')
      expect(races[0]!.find('summary').text()).not.toContain('Warmup')
      expect(races[1]!.find('summary').text()).toContain('Warmup')
      expect(races[0]!.findAll('tbody tr')).toHaveLength(5)
    })

    it('names the best player shown when a hidden bot won', () => {
      const botWon = {
        ...race,
        entries: race.entries.map((e) => (e.position === 1 ? { ...e, isBot: true, name: 'Speedy AI' } : e.position === 2 ? { ...e, isBot: false, name: 'Ann' } : e)),
      }
      state.items = [asCup(cup({ races: [botWon] }))]

      expect(mount(RecentRaces).find('.cup-race-list summary').text()).toContain('best player Ann (P2)')
    })

    it('has no bots switch without bots, and says when only bots scored', () => {
      const humans = { ...race, entries: race.entries.filter((e) => !e.isBot) }
      state.items = [asCup(cup({ standings: [{ position: 1, name: 'Alice', isBot: false, points: 3 }], races: [humans] }))]
      expect(mount(RecentRaces).find('.bots-switch').exists()).toBe(false)

      state.items = [asCup(cup({ standings: [{ position: 1, name: 'Bot Ann', isBot: true, points: 3 }] }))]
      expect(mount(RecentRaces).text()).toContain('Only bots have cup points. Turn on Show bots to see them.')
    })

    it('says when the standings begin during the warmup', () => {
      state.items = [asCup(cup({ status: 'Warmup', startsAt: '2026-10-09T19:00:00Z', standings: [], raceCount: 0, races: [] }))]

      const card = mount(RecentRaces).find('.cup')
      expect(card.text()).toContain('WARMUP')
      expect(card.text()).toContain('Starts ')
      expect(card.text()).toContain('The standings begin when the cup starts.')
      expect(card.find('.cup-races').exists()).toBe(false)
    })

    it('tells a cup without cup points from one with no races yet, or one that never started', () => {
      state.items = [asCup(cup({ status: 'Running', standings: [] }))]
      expect(mount(RecentRaces).text()).toContain('No cup points were awarded.')

      state.items = [asCup(cup({ status: 'Running', standings: [], races: [{ ...race, warmup: true }], raceCount: 1 }))]
      expect(mount(RecentRaces).text()).toContain('No races yet.')

      state.items = [asCup(cup({ standings: [], races: [{ ...race, warmup: true }], raceCount: 1 }))]
      expect(mount(RecentRaces).text()).toContain('The cup never started: its races were all in the warmup.')
    })

    it('says when only the latest races are listed', () => {
      state.items = [asCup(cup({ raceCount: 60 }))]
      expect(mount(RecentRaces).find('.cup-meta').text()).toContain('60 races, the latest 2 listed')
    })
  })
})
