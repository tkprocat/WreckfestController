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
  // the first answer arrives while the second is still on its way. It predates the event,
  // so it must not be shown - Update would be offered for a running server.
  it('ignores an earlier answer while a later load is still on its way', async () => {
    const first = deferred<ReturnType<typeof ok>>()
    const second = deferred<ReturnType<typeof ok>>()
    api.GET.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise)
    const { status: current, load } = useServerStatus()

    const opening = load()
    const afterEvent = load()
    first.resolve(ok(status(false)))
    await opening

    expect(current.value).toBeNull()

    second.resolve(ok(status(true)))
    await afterEvent
    expect(current.value?.isRunning).toBe(true)
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
