import { beforeEach, describe, expect, it, vi } from 'vitest'
import { useServerStatus, type ServerStatus } from './useServerStatus'

const api = vi.hoisted(() => ({ GET: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((res) => (resolve = res))
  return { promise, resolve }
}

const status = (isRunning: boolean): ServerStatus =>
  ({ isRunning, processId: isRunning ? 42 : null, uptimeSeconds: null, currentTrack: null }) as ServerStatus
const ok = (value: ServerStatus) => ({ data: value, error: undefined, response: new Response(null, { status: 200 }) })

beforeEach(() => api.GET.mockReset())

describe('useServerStatus', () => {
  // The page opens (Running on its way), then a hub event says Stopped and answers first:
  // the late Running must not bring back buttons for a server that is gone.
  it('drops an answer older than one already applied', async () => {
    const first = deferred<ReturnType<typeof ok>>()
    const second = deferred<ReturnType<typeof ok>>()
    api.GET.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const { status: current, load } = useServerStatus()

    const opening = load()
    const afterEvent = load()
    second.resolve(ok(status(false)))
    await afterEvent
    first.resolve(ok(status(true)))
    await opening

    expect(current.value?.isRunning).toBe(false)
  })

  // The page opens (Stopped captured), a hub event says the server started and asks again;
  // the first answer arrives while the second is still on its way. It may be shown, but it
  // predates the event, so the page is still refreshing and must not act on it yet.
  it('is still refreshing while a later load is on its way', async () => {
    const first = deferred<ReturnType<typeof ok>>()
    const second = deferred<ReturnType<typeof ok>>()
    api.GET.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const { status: current, refreshing, load } = useServerStatus()

    const opening = load()
    const afterEvent = load()
    first.resolve(ok(status(false)))
    await opening

    expect(refreshing.value).toBe(true)

    second.resolve(ok(status(true)))
    await afterEvent
    expect(refreshing.value).toBe(false)
    expect(current.value?.isRunning).toBe(true)
  })

  // Loads that keep starting before the last one answers must not starve the page: each
  // answer newer than the last applied one is shown.
  it('keeps moving forward when loads keep overlapping', async () => {
    const answers = [deferred<ReturnType<typeof ok>>(), deferred<ReturnType<typeof ok>>(), deferred<ReturnType<typeof ok>>()]
    answers.forEach((a) => api.GET.mockReturnValueOnce(a.promise))
    const { status: current, load } = useServerStatus()

    const one = load()
    const two = load()
    answers[0].resolve(ok(status(false)))
    await one
    expect(current.value?.isRunning).toBe(false)

    const three = load()
    answers[1].resolve(ok(status(true)))
    await two
    expect(current.value?.isRunning).toBe(true)

    answers[2].resolve(ok(status(true)))
    await three
  })

  // A load that never answers would leave the page refreshing for good: each has a timeout.
  it('asks with a timeout', async () => {
    api.GET.mockResolvedValueOnce(ok(status(false)))
    await useServerStatus().load()

    expect(api.GET).toHaveBeenCalledWith('/api/server/status', { signal: expect.any(AbortSignal) })
  })

  // Unknown, not the last state seen: nothing may be offered for a server it cannot see.
  it('makes the state unknown when a load fails', async () => {
    const { status: current, error, load } = useServerStatus()
    api.GET.mockResolvedValueOnce(ok(status(false)))
    await load()

    api.GET.mockRejectedValueOnce(new TypeError('Failed to fetch'))
    await load()

    expect(current.value).toBeNull()
    expect(error.value).toBe('The controller cannot be reached.')
  })
})
