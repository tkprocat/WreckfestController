/** "2h 05m", "12m", "45s": how long the server has been up. */
export function formatUptime(seconds: number | null | undefined): string {
  if (seconds == null || seconds < 0) {
    return ''
  }

  const days = Math.floor(seconds / 86_400)
  const hours = Math.floor((seconds % 86_400) / 3_600)
  const minutes = Math.floor((seconds % 3_600) / 60)
  if (days > 0) {
    return `${days}d ${hours}h`
  }

  if (hours > 0) {
    return `${hours}h ${String(minutes).padStart(2, '0')}m`
  }

  return minutes > 0 ? `${minutes}m` : `${Math.floor(seconds)}s`
}

/** A UTC timestamp from the API, in the viewer's own time zone: "Fri 2 Oct, 20:00". */
export function formatWhen(iso: string, locale?: string): string {
  return new Intl.DateTimeFormat(locale, {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(iso))
}

/** "in 3 hours", "in 2 days", "now": how far off an occurrence is. */
export function formatFromNow(iso: string, now: Date = new Date(), locale?: string): string {
  const seconds = (new Date(iso).getTime() - now.getTime()) / 1000
  if (Math.abs(seconds) < 60) {
    return 'now'
  }

  const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })
  const units: [Intl.RelativeTimeFormatUnit, number][] = [
    ['day', 86_400],
    ['hour', 3_600],
    ['minute', 60],
  ]
  for (const [unit, size] of units) {
    if (Math.abs(seconds) >= size) {
      return format.format(Math.round(seconds / size), unit)
    }
  }

  return 'now'
}
