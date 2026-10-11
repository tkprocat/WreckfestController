import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'
import { readonly, ref } from 'vue'

// Live updates from /hubs/server. Everyone gets the public group; a signed-in admin also
// gets the admin group (the console log, cup outcomes). The server decides the groups when
// the connection starts, so restart it after signing in or out.
//
// These types mirror Hubs/IServerHubClient.cs: the hub is not in the OpenAPI contract.

export interface PlayerSummary {
  name: string
  playerId: number | null
  score: number | null
  vehicle: string | null
  slot: number | null
  isBot: boolean
  joinedAt: string
}

export interface HubEvents {
  PlayersUpdated: { players: PlayerSummary[] }
  PlayerJoined: { playerName: string; isBot: boolean }
  PlayerLeft: { playerName: string }
  TrackChanged: { trackId: string }
  CupActivated: { cupId: number; cupName: string; timestamp: string }
  CupStarted: { cupId: number; cupName: string; timestamp: string }
  CupEnded: { cupId: number; cupName: string; timestamp: string }
  ServerStarted: { processId: number; processName: string; startTime: string; timestamp: string }
  ServerStopped: { processId: number; stopMethod: string; timestamp: string }
  ServerRestarted: { oldProcessId: number | null; newProcessId: number; restartMethod: string; timestamp: string }
  ServerAttached: { processId: number; processName: string; startTime: string; timestamp: string }
  ServerRestartPending: {
    minutesRemaining: number
    cupName: string | null
    cupId: number | null
    scheduledRestartTime: string | null
    timestamp: string
  }
  RaceRecorded: { raceId: number; trackId: string; endedAt: string }
  // Admin group only.
  ConsoleLog: { logs: string[] }
  CupOccurrenceEnded: { cupId: number; cupName: string; occurrence: string; outcome: string; timestamp: string }
  // Client-side only: the connection is back after being lost, so anything sent meanwhile
  // was missed. A page refetches what it shows.
  Reconnected: Record<string, never>
}

export const HUB_ROUTE = '/hubs/server'

/**
 * How long to wait before the next try: quick at first, then every 30 s for as long as it
 * takes. The library's default gives up after four tries (#231), which left the admin
 * pages silently showing a frozen console and roster after a controller restart.
 */
export function retryDelay(previousRetryCount: number): number {
  return [0, 2_000, 5_000, 10_000][previousRetryCount] ?? 30_000
}

/** `reconnecting`: the connection was lost, or never made, and is being retried. */
export type HubStatus = 'connecting' | 'connected' | 'reconnecting'
const status = ref<HubStatus>('connecting')
export const hubStatus = readonly(status)

let connection: HubConnection | null = null
let retryTimer: ReturnType<typeof setTimeout> | undefined
let retries = 0
// Whether updates may have been missed since the connection last ran: set by a drop, a
// close or a failed start, and cleared only by the connect that follows. Kept apart from
// the shown status, so a deliberate restart neither hides a loss nor makes one up.
let lost = false
// Bumped by restartHub: a start it cancels on purpose is not a loss.
let attempt = 0
// restartHub's stop: the close it causes is not a loss either.
let stopping = false
const reconnectedHandlers = new Set<() => void>()

function hub(): HubConnection {
  if (!connection) {
    connection = new HubConnectionBuilder()
      .withUrl(HUB_ROUTE)
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ({ previousRetryCount }) => retryDelay(previousRetryCount) })
      .configureLogging(LogLevel.Warning)
      .build()
    connection.onreconnecting(markLost)
    connection.onreconnected(connected)
    // The retry policy never gives up, but a reconnect the server refuses still ends
    // here: start again from scratch.
    connection.onclose(() => {
      if (!stopping) {
        markLost()
        scheduleStart()
      }
    })
  }
  return connection
}

function markLost(): void {
  lost = true
  status.value = 'reconnecting'
}

function connected(): void {
  const recovered = lost
  lost = false
  status.value = 'connected'
  retries = 0
  if (recovered) {
    for (const handler of reconnectedHandlers) {
      try {
        handler()
      } catch {
        // One page's refetch must not keep the others from theirs.
      }
    }
  }
}

function scheduleStart(): void {
  clearTimeout(retryTimer)
  retryTimer = setTimeout(() => void startHub().catch(() => undefined), retryDelay(retries++))
}

/** Listens for one message. Returns a function that stops listening. */
export function onHub<E extends keyof HubEvents>(event: E, handler: (message: HubEvents[E]) => void): () => void {
  if (event === 'Reconnected') {
    const resync = () => handler({} as HubEvents[E])
    reconnectedHandlers.add(resync)
    return () => reconnectedHandlers.delete(resync)
  }

  const connection = hub()
  connection.on(event, handler)
  return () => connection.off(event, handler)
}

/**
 * Connects, if not already connected. Safe to call more than once. A failed start is
 * retried in the background until it succeeds; the returned promise still rejects.
 */
export async function startHub(): Promise<void> {
  const connection = hub()
  if (connection.state !== HubConnectionState.Disconnected) {
    return
  }

  clearTimeout(retryTimer)
  const mine = attempt
  try {
    await connection.start()
  } catch (error) {
    // Cancelled by restartHub, which starts again itself: nothing was lost.
    if (mine === attempt) {
      markLost()
      scheduleStart()
    }
    throw error
  }

  if (mine === attempt) {
    connected()
  }
}

/** Reconnects so the server puts this connection in the groups for who is now signed in. */
export async function restartHub(): Promise<void> {
  const connection = hub()
  clearTimeout(retryTimer)
  attempt++
  // A loss already under way still is one: its pages refetch once this connects.
  if (!lost) {
    status.value = 'connecting'
  }
  if (connection.state !== HubConnectionState.Disconnected) {
    stopping = true
    try {
      await connection.stop()
    } finally {
      stopping = false
    }
  }

  await startHub()
}
