import type { PublicRace, PublicRaceEntry } from '@/composables/usePublicFeed'
import { formatRaceTime } from './format'

/** The first three placed cars, bots included: they keep their real places. */
export function podium(race: PublicRace): PublicRaceEntry[] {
  return race.entries.filter((e) => e.position != null).slice(0, 3)
}

/** A car's time as the results show it: "3:05.120", "3:05.120 est.", "DNF" or "–". */
export function result(entry: PublicRaceEntry): string {
  if (entry.outcome === 'DidNotFinish') return 'DNF'
  const time = formatRaceTime(entry.timeMs)
  if (!time) return '–'
  return entry.outcome === 'Projected' ? time + ' est.' : time
}

export function resultTitle(entry: PublicRaceEntry): string | undefined {
  if (entry.outcome === 'Projected') return "Estimated by the game: the bot hadn't finished when the race ended"
  if (entry.outcome === 'DidNotFinish') return 'Did not finish'
  return undefined
}

export function laps(race: PublicRace): string {
  return race.laps > 0 ? `${race.laps} lap${race.laps === 1 ? '' : 's'}` : ''
}
