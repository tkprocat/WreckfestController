import type { components } from '@/api/schema'

type Origin = components['schemas']['TrackOrigin']

const ORIGINS: Record<Origin, string> = { BaseGame: 'Base game', Dlc: 'DLC', Workshop: 'Workshop', Custom: 'Custom' }

/** "Base game", or "Workshop · The Very TrackPack 2" for a mod's track. */
export function originLabel(track: { origin: Origin; modName?: string | null }): string {
  const origin = ORIGINS[track.origin] ?? track.origin
  return track.modName ? `${origin} · ${track.modName}` : origin
}

/** One lap in km, such as "3.91 km", or '' when it is not known. */
export function lengthLabel(metres: number | null | undefined): string {
  return metres && metres > 0 ? `${(metres / 1000).toFixed(2)} km` : ''
}

/** About how long `laps` laps take at this typical lap, to the second: "about 7:40", "about 1:02:05". */
export function estimateLabel(typicalLapMs: number | null | undefined, laps: number): string {
  if (!typicalLapMs || typicalLapMs <= 0 || !Number.isFinite(laps) || laps < 1) return ''
  const total = Math.round((typicalLapMs * laps) / 1000)
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const seconds = String(total % 60).padStart(2, '0')
  return hours > 0 ? `about ${hours}:${String(minutes).padStart(2, '0')}:${seconds}` : `about ${minutes}:${seconds}`
}
