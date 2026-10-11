import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

// A stand-in for the SignalR connection: what the builder was given, and the callbacks
// the module registered, so a test can play the connection's life out by hand.
const fake = vi.hoisted(() => ({
  retryPolicy: null as null | { nextRetryDelayInMilliseconds: (context: { previousRetryCount: number }) => number | null },
  state: 'Disconnected',
  start: vi.fn(),
  stop: vi.fn(),
  on: vi.fn(),
  off: vi.fn(),
  reconnecting: [] as (() => void)[],
  reconnected: [] as (() => void)[],
  closed: [] as (() => void)[],
}))

vi.mock('@microsoft/signalr', () => {
  const connection = {
    get state() {
      return fake.state
    },
    start: (...args: unknown[]) => fake.start(...args),
    stop: (...args: unknown[]) => fake.stop(...args),
    on: (...args: unknown[]) => fake.on(...args),
    off: (...args: unknown[]) => fake.off(...args),
    onreconnecting: (callback: () => void) => fake.reconnecting.push(callback),
    onreconnected: (callback: () => void) => fake.reconnected.push(callback),
    onclose: (callback: () => void) => fake.closed.push(callback),
  }
  class HubConnectionBuilder {
    withUrl() {
      return this
    }
    withAutomaticReconnect(policy: typeof fake.retryPolicy) {
      fake.retryPolicy = policy
      return this
    }
    configureLogging() {
      return this
    }
    build() {
      return connection
    }
  }
  return {
    HubConnectionBuilder,
    HubConnectionState: { Disconnected: 'Disconnected', Connected: 'Connected' },
    LogLevel: { Warning: 3 },
  }
})

type Hub = typeof import('./hub')
let hub: Hub

beforeEach(async () => {
  vi.useFakeTimers()
  vi.resetModules()
  Object.assign(fake, { retryPolicy: null, state: 'Disconnected', reconnecting: [], reconnected: [], closed: [] })
  fake.start.mockReset().mockImplementation(async () => {
    fake.state = 'Connected'
  })
  fake.stop.mockReset().mockImplementation(async () => {
    fake.state = 'Disconnected'
    fake.closed.forEach((callback) => callback())
  })
  hub = await import('./hub')
})

afterEach(() => {
  vi.useRealTimers()
})

/** The connection drops and the library reconnects it. */
function dropAndReconnect() {
  fake.reconnecting.forEach((callback) => callback())
  fake.reconnected.forEach((callback) => callback())
}

describe('hub', () => {
  // The library's default gives up after four tries, which froze the admin pages (#231).
  it('never stops retrying, settling at every 30 seconds', async () => {
    await hub.startHub()

    const delay = (previousRetryCount: number) => fake.retryPolicy!.nextRetryDelayInMilliseconds({ previousRetryCount })
    expect([0, 1, 2, 3].map(delay)).toEqual([0, 2_000, 5_000, 10_000])
    expect(delay(4)).toBe(30_000)
    expect(delay(1_000)).toBe(30_000)
  })

  it('tells listeners when the connection comes back, but not on the first connect', async () => {
    const resyncs = vi.fn()
    hub.onHub('Reconnected', resyncs)
    await hub.startHub()
    expect(resyncs).not.toHaveBeenCalled()

    fake.reconnecting.forEach((callback) => callback())
    expect(hub.hubStatus.value).toBe('reconnecting')
    fake.reconnected.forEach((callback) => callback())

    expect(resyncs).toHaveBeenCalledTimes(1)
    expect(hub.hubStatus.value).toBe('connected')
  })

  it('stops telling a listener once it stops listening', async () => {
    const resyncs = vi.fn()
    const stop = hub.onHub('Reconnected', resyncs)
    await hub.startHub()

    stop()
    dropAndReconnect()

    expect(resyncs).not.toHaveBeenCalled()
  })

  it('keeps trying a first start that failed, and resyncs once it connects', async () => {
    const resyncs = vi.fn()
    hub.onHub('Reconnected', resyncs)
    fake.start.mockRejectedValueOnce(new Error('down')).mockRejectedValueOnce(new Error('still down'))

    await expect(hub.startHub()).rejects.toThrow('down')
    expect(hub.hubStatus.value).toBe('reconnecting')

    await vi.advanceTimersByTimeAsync(0)
    expect(fake.start).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(2_000)
    expect(fake.start).toHaveBeenCalledTimes(3)

    expect(hub.hubStatus.value).toBe('connected')
    expect(resyncs).toHaveBeenCalledTimes(1)
  })

  // A reconnect the server refuses closes the connection despite the retry policy.
  it('starts again after the connection closes on its own', async () => {
    await hub.startHub()
    fake.state = 'Disconnected'

    fake.closed.forEach((callback) => callback())
    expect(hub.hubStatus.value).toBe('reconnecting')
    await vi.advanceTimersByTimeAsync(0)

    expect(fake.start).toHaveBeenCalledTimes(2)
    expect(hub.hubStatus.value).toBe('connected')
  })

  // Signing out while the connection is down: the restart must not hide that updates
  // were missed.
  it('still resyncs a loss that a deliberate restart lands in', async () => {
    const resyncs = vi.fn()
    hub.onHub('Reconnected', resyncs)
    await hub.startHub()
    fake.reconnecting.forEach((callback) => callback())

    await hub.restartHub()

    expect(resyncs).toHaveBeenCalledTimes(1)
    expect(hub.hubStatus.value).toBe('connected')
  })

  // A restart while the first start is still on its way cancels it: that is not a loss.
  it('does not take a start a restart cancelled for a loss', async () => {
    const resyncs = vi.fn()
    hub.onHub('Reconnected', resyncs)
    let cancelFirst!: (error: Error) => void
    fake.start.mockImplementationOnce(
      () => new Promise<void>((_, reject) => (cancelFirst = reject)),
    )
    fake.stop.mockImplementationOnce(async () => {
      fake.state = 'Disconnected'
      cancelFirst(new Error('The connection was stopped during negotiation.'))
    })
    fake.state = 'Disconnected'
    const first = hub.startHub()
    fake.state = 'Connecting'

    await hub.restartHub()
    await expect(first).rejects.toThrow('stopped during negotiation')
    await vi.advanceTimersByTimeAsync(60_000)

    expect(resyncs).not.toHaveBeenCalled()
    expect(fake.start).toHaveBeenCalledTimes(2)
    expect(hub.hubStatus.value).toBe('connected')
  })

  // Signing in or out restarts it on purpose: nothing was lost, so nothing to refetch.
  it('does not retry or resync around a deliberate restart', async () => {
    const resyncs = vi.fn()
    hub.onHub('Reconnected', resyncs)
    await hub.startHub()

    await hub.restartHub()
    await vi.advanceTimersByTimeAsync(60_000)

    expect(fake.start).toHaveBeenCalledTimes(2)
    expect(resyncs).not.toHaveBeenCalled()
    expect(hub.hubStatus.value).toBe('connected')
  })
})
