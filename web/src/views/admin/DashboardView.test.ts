import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { NDataTable } from 'naive-ui'
import DashboardView from './DashboardView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn() }))
const hub = vi.hoisted(() => new Map<string, (message: unknown) => void>())

vi.mock('@/api/client', () => ({ api }))
vi.mock('@/realtime/hub', () => ({
  onHub: (event: string, handler: (message: unknown) => void) => {
    hub.set(event, handler)
    return () => hub.delete(event)
  },
}))

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((res) => (resolve = res))
  return { promise, resolve }
}

const answer = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) })
const player = (name: string) => ({ name, playerId: null, score: null, vehicle: null, slot: null, isBot: false, joinedAt: '' })

beforeEach(() => {
  api.GET.mockReset()
  hub.clear()
})

describe('DashboardView', () => {
  // The hub's roster is newer than a load that was on its way: the load must not undo it.
  it('keeps a hub roster that arrived while the players were loading', async () => {
    const players = deferred<unknown>()
    api.GET.mockImplementation((path: string) =>
      path === '/api/server/players'
        ? players.promise
        : Promise.resolve(answer({ isRunning: true, processId: 42, uptimeSeconds: 60, currentTrack: null })),
    )
    const wrapper = mount(DashboardView)
    await flushPromises()

    hub.get('PlayersUpdated')!({ players: [player('From the hub')] })
    players.resolve(answer({ players: [player('Stale')], totalPlayers: 1, maxPlayers: 24, lastUpdated: '' }))
    await flushPromises()

    expect(wrapper.text()).toContain('From the hub')
    expect(wrapper.text()).not.toContain('Stale')
    wrapper.unmount()
  })
  // A player id and another player's slot can be the same number: the rows must still differ.
  it('keys roster rows so an id never collides with a slot or a name', async () => {
    api.GET.mockImplementation((path: string) =>
      path === '/api/server/players'
        ? Promise.resolve(answer({ players: [], totalPlayers: 0, maxPlayers: 24, lastUpdated: '' }))
        : Promise.resolve(answer({ isRunning: true, processId: 42, uptimeSeconds: 60, currentTrack: null })),
    )
    const wrapper = mount(DashboardView)
    await flushPromises()

    const rowKey = wrapper.findComponent(NDataTable).props('rowKey') as (p: unknown) => unknown
    const keys = [{ ...player('Alice'), playerId: 3 }, { ...player('Bob'), slot: 3 }, player('3')].map(rowKey)
    expect(new Set(keys).size).toBe(3)
    wrapper.unmount()
  })
  it('distinguishes a pending roster from a confirmed empty roster', async () => {
    const roster = deferred<unknown>()
    api.GET.mockImplementation((path: string) =>
      path === '/api/server/players'
        ? roster.promise
        : Promise.resolve(answer({ isRunning: false, processId: null, uptimeSeconds: null, currentTrack: null })),
    )
    const wrapper = mount(DashboardView)
    await flushPromises()
    expect(wrapper.findAll('.metric-card')[2].text()).toContain('Loading')
    expect(wrapper.findAll('.metric-card')[2].text()).not.toContain('0')
    roster.resolve(answer({ players: [], totalPlayers: 0, maxPlayers: 24, lastUpdated: '' }))
    await flushPromises()
    expect(wrapper.findAll('.metric-card')[2].text()).toContain('0')
    expect(wrapper.text()).toContain('No players connected.')
    expect(wrapper.text()).toContain('Server stopped')
    wrapper.unmount()
  })

  it('shows an unknown player count when the roster request fails', async () => {
    api.GET.mockImplementation((path: string) =>
      path === '/api/server/players'
        ? Promise.resolve({ data: undefined, error: { title: 'Roster unavailable' }, response: new Response(null, { status: 503 }) })
        : Promise.resolve(answer({ isRunning: true, processId: 42, uptimeSeconds: 60, currentTrack: null })),
    )
    const wrapper = mount(DashboardView)
    await flushPromises()
    expect(wrapper.findAll('.metric-card')[2].text()).toContain('Unknown')
    expect(wrapper.text()).toContain('Player roster unavailable.')
    expect(wrapper.text()).toContain('Between races')
    wrapper.unmount()
  })

  it('keeps a confirmed hub roster when the earlier request fails', async () => {
    const roster = deferred<unknown>()
    api.GET.mockImplementation((path: string) =>
      path === '/api/server/players'
        ? roster.promise
        : Promise.resolve(answer({ isRunning: true, processId: 42, uptimeSeconds: 60, currentTrack: 'Fields' })),
    )
    const wrapper = mount(DashboardView)
    await flushPromises()
    hub.get('PlayersUpdated')!({ players: [player('From the hub')] })
    roster.resolve({ data: undefined, error: { title: 'Old request failed' }, response: new Response(null, { status: 503 }) })
    await flushPromises()
    expect(wrapper.text()).toContain('From the hub')
    expect(wrapper.text()).not.toContain('Old request failed')
    wrapper.unmount()
  })

})
