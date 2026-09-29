import type { SelectOption } from 'naive-ui'
import type { components } from '@/api/schema'

export type ServerConfig = components['schemas']['ServerConfig']
export type ConfigField = keyof ServerConfig

/**
 * How each server_config.cfg setting is edited. Values, ranges and wording follow the
 * comments in the dedicated server's own initial_server_config.cfg - the authoritative
 * reference, not third-party guides. `log` is not here: it names a file, which the web
 * never sees or sets.
 */
export type FieldDef =
  | { field: ConfigField; label: string; kind: 'text'; max?: number; help?: string }
  | { field: ConfigField; label: string; kind: 'number'; min: number; max?: number; help?: string }
  | { field: ConfigField; label: string; kind: 'select'; options: readonly Option[]; help?: string }
  /** A 0/1 setting, shown as a switch: `on` is what 1 means. */
  | { field: ConfigField; label: string; kind: 'flag'; on: string; help?: string }

/** A select's choice: Naive UI's own option type, with a label and a value always set. */
export type Option = SelectOption & { label: string; value: string | number }

export interface Section {
  title: string
  fields: readonly FieldDef[]
}

const plain = (...values: string[]): Option[] => values.map((value) => ({ label: value, value }))

export const SESSION_MODES: readonly Option[] = [
  { label: 'Normal (no cup points)', value: 'normal' },
  { label: 'Qualifying sprint', value: 'qualify-sprint' },
  { label: 'Qualifying lap', value: 'qualify-lap' },
  ...plain('30p-aggr', '25p-aggr', '25p-mod', '24p-lin', '16p-lin', '10p-double', '10p-lin', '35p-folk'),
  ...plain('f1-1991', 'f1-2003', 'f1-2010', 'player_count_1'),
]

export const GRID_ORDERS: readonly Option[] = [
  { label: 'Random', value: 'random' },
  { label: 'Performance, fastest at the front', value: 'perf_normal' },
  { label: 'Performance, fastest at the back', value: 'perf_reverse' },
  { label: 'Qualifying result', value: 'qualifying' },
  { label: 'Cup points, most at the front', value: 'cup_normal' },
  { label: 'Cup points, most at the back', value: 'cup_reverse' },
]

export const SECTIONS: readonly Section[] = [
  {
    title: 'Server',
    fields: [
      { field: 'serverName', label: 'Server name', kind: 'text', max: 63 },
      { field: 'welcomeMessage', label: 'Welcome message', kind: 'text', max: 254 },
      { field: 'password', label: 'Password', kind: 'text', max: 31, help: 'Leave empty for a public server.' },
      { field: 'maxPlayers', label: 'Max players', kind: 'number', min: 1, max: 24 },
    ],
  },
  {
    title: 'Network',
    fields: [
      { field: 'steamPort', label: 'Steam port', kind: 'number', min: 1, max: 65535 },
      { field: 'gamePort', label: 'Game port', kind: 'number', min: 1, max: 65535 },
      {
        field: 'queryPort',
        label: 'Query port',
        kind: 'number',
        min: 1,
        max: 65535,
        help: 'LAN search only finds query ports 27015-27020 and 26900-26905.',
      },
      { field: 'lan', label: 'LAN only', kind: 'flag', on: 'LAN only' },
      { field: 'excludeFromQuickplay', label: 'Quick Match', kind: 'flag', on: 'Excluded from Quick Match' },
    ],
  },
  {
    title: 'Lobby and admins',
    fields: [
      { field: 'lobbyCountdown', label: 'Lobby countdown (seconds)', kind: 'number', min: 30, max: 127 },
      {
        field: 'readyPlayersRequired',
        label: 'Players ready to start (%)',
        kind: 'number',
        min: 0,
        max: 100,
        help: 'For the automatic countdown.',
      },
      { field: 'adminControl', label: 'Countdown', kind: 'flag', on: 'Admin starts the countdown' },
      { field: 'ownerDisabled', label: 'Owner', kind: 'flag', on: 'Nobody becomes owner by joining first' },
      { field: 'clearUsers', label: 'Privileges', kind: 'flag', on: 'Cleared when the server starts' },
      { field: 'adminSteamIds', label: 'Admin Steam IDs', kind: 'text', help: 'SteamID64s, comma separated.' },
      { field: 'opSteamIds', label: 'Moderator Steam IDs', kind: 'text', help: 'SteamID64s, comma separated.' },
      { field: 'enableTrackVote', label: 'Track vote', kind: 'flag', on: 'Players vote for the next event' },
      { field: 'disableIdleKick', label: 'Idle players', kind: 'flag', on: 'Not kicked' },
    ],
  },
  {
    title: 'Event',
    fields: [
      { field: 'track', label: 'Track', kind: 'text', help: 'The track id, as the tracks command lists it.' },
      {
        field: 'gamemode',
        label: 'Game mode',
        kind: 'select',
        options: plain('racing', 'derby', 'derby deathmatch', 'team derby', 'team race', 'elimination race'),
      },
      { field: 'laps', label: 'Laps', kind: 'number', min: 1, max: 60 },
      // The reference gives the unit but no range.
      { field: 'timeLimit', label: 'Deathmatch time limit (minutes)', kind: 'number', min: 1 },
      {
        field: 'eliminationInterval',
        label: 'Elimination interval',
        kind: 'select',
        options: [
          { label: 'Every lap', value: 0 },
          ...[20, 30, 45, 60, 90, 120].map((s) => ({ label: `${s} seconds`, value: s })),
        ],
      },
      { field: 'numTeams', label: 'Teams', kind: 'number', min: 2, max: 4 },
      { field: 'bots', label: 'AI bots', kind: 'number', min: 0, max: 24 },
      { field: 'aiDifficulty', label: 'AI difficulty', kind: 'select', options: plain('novice', 'amateur', 'expert') },
      {
        field: 'vehicleDamage',
        label: 'Vehicle damage',
        kind: 'select',
        options: plain('normal', 'intense', 'realistic', 'extreme'),
      },
      { field: 'weather', label: 'Weather', kind: 'text', help: 'Empty for random weather.' },
    ],
  },
  {
    title: 'Cars',
    fields: [
      {
        field: 'carClassRestriction',
        label: 'Highest car class',
        kind: 'select',
        options: [{ label: 'Any class', value: '' }, ...plain('a', 'b', 'c')],
      },
      { field: 'carRestriction', label: 'Only this car', kind: 'text', help: 'Empty for any car.' },
      { field: 'specialVehiclesDisabled', label: 'Special vehicles', kind: 'flag', on: 'Not allowed' },
      { field: 'carResetDisabled', label: 'Car reset', kind: 'flag', on: 'Disabled' },
      { field: 'carResetDelay', label: 'Car reset delay (seconds)', kind: 'number', min: 0, max: 20 },
      { field: 'wrongWayLimiterDisabled', label: 'Wrong-way speed limiter', kind: 'flag', on: 'Disabled' },
    ],
  },
  {
    title: 'Cup and server',
    fields: [
      { field: 'sessionMode', label: 'Session mode', kind: 'select', options: SESSION_MODES },
      { field: 'gridOrder', label: 'Grid order', kind: 'select', options: GRID_ORDERS },
      { field: 'frequency', label: 'Update frequency', kind: 'select', options: plain('low', 'high') },
      { field: 'mods', label: 'Mods', kind: 'text', help: 'Mod folder names, comma separated.' },
    ],
  },
]

export const FIELDS: readonly FieldDef[] = SECTIONS.flatMap((section) => section.fields)

/**
 * A summary of the settings and the rotation in server_config.cfg's own format, as the
 * controller reads them - not the file byte for byte (the web never reads files). A
 * setting the file has no active line for is shown commented out, as it is in the file:
 * the server does not use it, and Save cannot change it.
 */
export function previewText(
  config: ServerConfig,
  keys: Record<string, string>,
  rotation: { collectionName: string; tracks: components['schemas']['EventLoopTrack'][] } | null,
  inactive: ReadonlySet<string> = new Set(),
): string {
  const lines: string[] = ['# Wreckfest server settings, as the controller reads them (a summary, not the file)', '']
  for (const section of SECTIONS) {
    lines.push(`# ${section.title}`)
    for (const def of section.fields) {
      const line = `${keys[def.field] ?? def.field}=${config[def.field] ?? ''}`
      lines.push(inactive.has(def.field) ? `#${line}   (not active in server_config.cfg)` : line)
    }

    lines.push('')
  }

  lines.push('# Event Loop')
  if (rotation) {
    lines.push(`#CollectionName ${rotation.collectionName}`)
    rotation.tracks.forEach((track, index) => {
      lines.push('', `## Add event ${index + 1} to Loop`, `el_add=${track.track}`)
      const extras: [string, unknown][] = [
        ['el_gamemode', track.gamemode],
        ['el_laps', track.laps],
        ['el_bots', track.bots],
        ['el_num_teams', track.numTeams],
        ['el_car_reset_disabled', track.carResetDisabled],
        ['el_wrong_way_limiter_disabled', track.wrongWayLimiterDisabled],
        ['el_car_class_restriction', track.carClassRestriction],
        ['el_car_restriction', track.carRestriction],
        ['el_weather', track.weather],
      ]
      for (const [key, value] of extras) {
        if (value !== null && value !== undefined) {
          lines.push(`${key}=${value}`)
        }
      }
    })
  }

  return lines.join('\n')
}
