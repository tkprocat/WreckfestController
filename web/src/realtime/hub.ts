import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'

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
  // Admin group only.
  ConsoleLog: { logs: string[] }
  CupOccurrenceEnded: { cupId: number; cupName: string; occurrence: string; outcome: string; timestamp: string }
}

export const HUB_ROUTE = '/hubs/server'

let connection: HubConnection | null = null

function hub(): HubConnection {
  connection ??= new HubConnectionBuilder()
    .withUrl(HUB_ROUTE)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
  return connection
}

/** Listens for one message. Returns a function that stops listening. */
export function onHub<E extends keyof HubEvents>(event: E, handler: (message: HubEvents[E]) => void): () => void {
  const connection = hub()
  connection.on(event, handler)
  return () => connection.off(event, handler)
}

/** Connects, if not already connected. Safe to call more than once. */
export async function startHub(): Promise<void> {
  const connection = hub()
  if (connection.state === HubConnectionState.Disconnected) {
    await connection.start()
  }
}

/** Reconnects so the server puts this connection in the groups for who is now signed in. */
export async function restartHub(): Promise<void> {
  const connection = hub()
  if (connection.state !== HubConnectionState.Disconnected) {
    await connection.stop()
  }

  await connection.start()
}
