import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { usePublicRaces, type PublicRace } from './usePublicRaces'

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

const race = (id: number) => ({ id, track: { id: 't', name: 'Track ' + id }, entries: [] }) as unknown as PublicRace
const ok = (data: PublicRace[]) => ({ data, response: new Response(null, { status: 200 }) })
const failed = (status: number) => ({ data: undefined, response: new Response(null, { status }) })

function mountRaces() {
  let state!: ReturnType<typeof usePublicRaces>
  const wrapper = mount(
    defineComponent({
      setup() {
        state = usePublicRaces()
        return () => h('div')
      },
    }),
  )
  return { wrapper, state: () => state }
}

beforeEach(() => {
  api.GET.mockReset()
  hub.handlers.clear()
  hub.unsubscribed = []
})

afterEach(() => vi.useRealTimers())

describe('usePublicRaces', () => {
  it('loads the races', async () => {
    api.GET.mockResolvedValueOnce(ok([race(2), race(1)]))

    const { state } = mountRaces()
    await flushPromises()

    expect(api.GET).toHaveBeenCalledWith('/api/public/races')
    expect(state().loading.value).toBe(false)
    expect(state().races.value?.map((r) => r.id)).toEqual([2, 1])
  })

  it('keeps the last list and reports a later failure', async () => {
    api.GET.mockResolvedValueOnce(ok([race(1)]))
    const { state } = mountRaces()
    await flushPromises()

    api.GET.mockResolvedValueOnce(failed(503))
    await state().reload()

    expect(state().races.value?.map((r) => r.id)).toEqual([1])
    expect(state().error.value).toBe('Recent races could not be loaded.')

    api.GET.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    await state().reload()
    expect(state().error.value).toBe('Recent races could not be loaded.')
  })

  it('reloads once after a newly recorded race', async () => {
    vi.useFakeTimers()
    api.GET.mockResolvedValue(ok([race(1)]))
    mountRaces()
    await flushPromises()

    hub.handlers.get('RaceRecorded')!({ raceId: 2 })
    hub.handlers.get('RaceRecorded')!({ raceId: 3 })
    await vi.advanceTimersByTimeAsync(2_000)

    expect(api.GET).toHaveBeenCalledTimes(2)
  })

  it('stops listening and polling when unmounted', async () => {
    vi.useFakeTimers()
    api.GET.mockResolvedValue(ok([]))
    const { wrapper } = mountRaces()
    await flushPromises()
    hub.handlers.get('RaceRecorded')!({ raceId: 2 })

    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(600_000)

    expect(api.GET).toHaveBeenCalledTimes(1)
    expect(hub.unsubscribed).toEqual(['RaceRecorded'])
  })
})
