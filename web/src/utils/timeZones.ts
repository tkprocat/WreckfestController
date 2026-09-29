import type { SelectOption } from 'naive-ui'

/**
 * The IANA time zones the browser knows, for a searchable select. The server checks the
 * value (an unknown zone is a field error), so this only has to be a good list to pick
 * from. "Europe/Copenhagen", shown as "Europe / Copenhagen".
 */
export function timeZoneOptions(): SelectOption[] {
  const zones = typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : []
  return zones.map((zone) => ({ label: zone.replaceAll('/', ' / ').replaceAll('_', ' '), value: zone }))
}

/** The browser's own time zone, as a sensible default for a new account. */
export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone
}
