import { describe, expect, it } from 'vitest'
import { formatFromNow, formatRaceTime, formatUptime } from './format'

describe('formatUptime', () => {
  it('uses the largest units that matter', () => {
    expect(formatUptime(45)).toBe('45s')
    expect(formatUptime(12 * 60 + 5)).toBe('12m')
    expect(formatUptime(2 * 3600 + 5 * 60)).toBe('2h 05m')
    expect(formatUptime(3 * 86_400 + 4 * 3600)).toBe('3d 4h')
    expect(formatUptime(null)).toBe('')
  })
})

describe('formatFromNow', () => {
  const now = new Date('2026-10-02T18:00:00Z')

  it('says how far off an occurrence is', () => {
    expect(formatFromNow('2026-10-02T18:00:30Z', now, 'en')).toBe('now')
    expect(formatFromNow('2026-10-02T21:00:00Z', now, 'en')).toBe('in 3 hours')
    expect(formatFromNow('2026-10-04T18:00:00Z', now, 'en')).toBe('in 2 days')
  })
})

describe('formatRaceTime', () => {
  it('shows minutes only when there are any, and always milliseconds', () => {
    expect(formatRaceTime(185_120)).toBe('3:05.120')
    expect(formatRaceTime(59_800)).toBe('59.800')
    expect(formatRaceTime(3_600_000 + 1)).toBe('60:00.001')
    expect(formatRaceTime(null)).toBe('')
  })
})
