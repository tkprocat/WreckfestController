import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { usePublicOverview, type PublicOverview } from './usePublicOverview'

const api = vi.hoisted(() => ({ GET: vi.fn() }))
const hub = vi.hoisted(() => ({
  handlers: new Map<string, (message: unknown) => void>(),
  unsubscribed: [] as string[],
}))

vi.mock('@/api/client', () => ({ api }))
vi.mock('@/realtime/hub', () => ({
  onHub: (event: string, handler: (message: unknown) => void) => {
    hub.handlers.set(event, handler)
    return () => hub.unsubscribed.push(event)
  },
}))

/** A promise the test settles when it wants, to put answers out of order. */
function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((res, rej) => {
    resolve = res
    reject = rej
  })
  return { promise, resolve, reject }
}

function overview(track: string, players: string[] = []): PublicOverview {
  return {
    serverName: 'Test',
    currentTrack: { name: track },
    players: { humans: players.length, bots: 0, list: players.map((name) => ({ name, isBot: false })) },
  } as unknown as PublicOverview
}

const ok = (data: PublicOverview) => ({ data, response: new Response(null, { status: 200 }) })
const failed = (status: number) => ({ data: undefined, response: new Response(null, { status }) })

function mountOverview() {
  let state!: ReturnType<typeof usePublicOverview>
  const wrapper = mount(
    defineComponent({
      setup() {
        state = usePublicOverview()
        return () => h('div')
      },
    }),
  )
  return { wrapper, state: () => state }
}

const playersUpdated = (...names: string[]) =>
  hub.handlers.get('PlayersUpdated')!({ players: names.map((name) => ({ name, isBot: false })) })

beforeEach(() => {
  api.GET.mockReset()
  hub.handlers.clear()
  hub.unsubscribed = []
})

afterEach(() => vi.useRealTimers())

describe('usePublicOverview', () => {
  it('stops loading and says so when the controller cannot be reached', async () => {
    api.GET.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    const { state } = mountOverview()
    await flushPromises()

    expect(state().loading.value).toBe(false)
    expect(state().error.value).toBe('The controller cannot be reached.')
  })

  // A later load that answers first must not be undone by the earlier one.
  it('drops an answer older than one already applied', async () => {
    const first = deferred<ReturnType<typeof ok>>()
    const second = deferred<ReturnType<typeof ok>>()
    api.GET.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)

    const { state } = mountOverview()
    void state().reload()
    second.resolve(ok(overview('Newer')))
    await flushPromises()
    first.resolve(ok(overview('Older')))
    await flushPromises()

    expect(state().overview.value?.currentTrack?.name).toBe('Newer')
  })

  // Players from the hub, received while a load was on its way, are newer than its answer.
  it('keeps hub players that arrived during a load, even the first one', async () => {
    const load = deferred<ReturnType<typeof ok>>()
    api.GET.mockReturnValueOnce(load.promise)

    const { state } = mountOverview()
    playersUpdated('Hub player')
    load.resolve(ok(overview('Track', ['Stale player'])))
    await flushPromises()

    expect(state().overview.value?.players.list.map((p) => p.name)).toEqual(['Hub player'])
  })

  it('takes the players from a load started after the hub update', async () => {
    api.GET.mockResolvedValueOnce(ok(overview('Track', ['First'])))
    const { state } = mountOverview()
    await flushPromises()
    playersUpdated('From hub')

    api.GET.mockResolvedValueOnce(ok(overview('Track', ['From a later load'])))
    await state().reload()

    expect(state().overview.value?.players.list.map((p) => p.name)).toEqual(['From a later load'])
  })

  // Stale data stays on screen, but with the reason it may be out of date.
  it('keeps the last overview and reports a later failure', async () => {
    api.GET.mockResolvedValueOnce(ok(overview('Track')))
    const { state } = mountOverview()
    await flushPromises()

    api.GET.mockResolvedValueOnce(failed(503))
    await state().reload()

    expect(state().overview.value?.currentTrack?.name).toBe('Track')
    expect(state().error.value).toBe("The controller's database is unavailable.")

    api.GET.mockResolvedValueOnce(ok(overview('Track')))
    await state().reload()
    expect(state().error.value).toBeNull()
  })

  it('stops listening and polling when unmounted', async () => {
    vi.useFakeTimers()
    api.GET.mockResolvedValue(ok(overview('Track')))
    const { wrapper } = mountOverview()
    await flushPromises()
    hub.handlers.get('TrackChanged')!({ trackId: 'x' })

    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(120_000)

    expect(api.GET).toHaveBeenCalledTimes(1)
    expect(hub.unsubscribed).toContain('PlayersUpdated')
    expect(hub.unsubscribed).toContain('TrackChanged')
  })

  it('reloads once after a burst of hub events settles', async () => {
    vi.useFakeTimers()
    api.GET.mockResolvedValue(ok(overview('Track')))
    mountOverview()
    await flushPromises()

    hub.handlers.get('TrackChanged')!({ trackId: 'a' })
    hub.handlers.get('ServerRestarted')!({})
    hub.handlers.get('TrackChanged')!({ trackId: 'b' })
    await vi.advanceTimersByTimeAsync(2_000)

    expect(api.GET).toHaveBeenCalledTimes(2)
  })
})
