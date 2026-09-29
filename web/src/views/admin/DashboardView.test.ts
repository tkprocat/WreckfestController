import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
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
})
