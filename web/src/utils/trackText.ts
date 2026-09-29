import type { components } from '@/api/schema'

type Track = components['schemas']['EventLoopTrack']

/** A track and every setting it saves with, so a conflict shows any setting that differs. */
export function describeTrack(t: Track): string {
  const settings = [
    t.gamemode,
    t.laps != null && `laps ${t.laps}`,
    t.bots != null && `bots ${t.bots}`,
    t.numTeams != null && `teams ${t.numTeams}`,
    t.carResetDisabled != null && `car reset ${t.carResetDisabled ? 'off' : 'on'}`,
    t.wrongWayLimiterDisabled != null && `wrong-way limiter ${t.wrongWayLimiterDisabled ? 'off' : 'on'}`,
    t.carClassRestriction && `class ${t.carClassRestriction}`,
    t.carRestriction && `car ${t.carRestriction}`,
    t.weather && `weather ${t.weather}`,
  ].filter(Boolean)
  return settings.length ? `${t.track} [${settings.join(' · ')}]` : (t.track ?? '')
}
