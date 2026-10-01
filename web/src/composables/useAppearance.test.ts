import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { effectScope, nextTick, type EffectScope } from 'vue'
import { APPEARANCE_KEY, useAppearance } from './useAppearance'
import { palettes } from '@/theme/tokens'

let scope: EffectScope
let listener: ((event: MediaQueryListEvent) => void) | undefined
let remove: ReturnType<typeof vi.fn>
let osDark = false

function start() {
  scope = effectScope()
  return scope.run(() => useAppearance())!
}

beforeEach(() => {
  localStorage.clear()
  osDark = false
  listener = undefined
  remove = vi.fn()
  vi.stubGlobal('matchMedia', vi.fn(() => ({
    matches: osDark,
    addEventListener: (_: string, callback: typeof listener) => { listener = callback },
    removeEventListener: remove,
  })))
})

afterEach(() => {
  scope?.stop()
  vi.restoreAllMocks()
  vi.unstubAllGlobals()
  document.documentElement.removeAttribute('style')
  delete document.documentElement.dataset.theme
})

describe('appearance', () => {
  it('defaults to System and applies OS changes to components, page colors and native controls', async () => {
    const appearance = start()
    expect(appearance.preference.value).toBe('system')
    expect(appearance.theme.value).toBeNull()
    listener!({ matches: true } as MediaQueryListEvent)
    await nextTick()
    expect(appearance.mode.value).toBe('dark')
    expect(appearance.theme.value).not.toBeNull()
    expect(appearance.overrides.value.common?.cardColor).toBe(palettes.dark.cardColor)
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(document.documentElement.style.colorScheme).toBe('dark')
    expect(document.documentElement.style.getPropertyValue('--surface')).toBe(palettes.dark.cardColor)
  })

  it('restores an explicit preference and ignores OS changes until System is selected', async () => {
    osDark = true
    localStorage.setItem(APPEARANCE_KEY, 'light')
    const appearance = start()
    expect(appearance.mode.value).toBe('light')
    listener!({ matches: true } as MediaQueryListEvent)
    await nextTick()
    expect(appearance.mode.value).toBe('light')
    appearance.setPreference('system')
    await nextTick()
    expect(appearance.mode.value).toBe('dark')
    expect(localStorage.getItem(APPEARANCE_KEY)).toBe('system')
  })

  it('persists a selected theme and restores it on the next mount', async () => {
    const appearance = start()
    appearance.setPreference('dark')
    await nextTick()
    scope.stop()
    expect(start().preference.value).toBe('dark')
  })

  it('falls back for invalid stored values and rejects invalid selections', () => {
    localStorage.setItem(APPEARANCE_KEY, 'invalid')
    const appearance = start()
    expect(appearance.preference.value).toBe('system')
    appearance.setPreference('invalid')
    expect(appearance.preference.value).toBe('system')
  })

  it('still switches theme when browser storage cannot be read or written', async () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    const appearance = start()
    appearance.setPreference('dark')
    await nextTick()
    expect(appearance.mode.value).toBe('dark')
    expect(document.documentElement.style.colorScheme).toBe('dark')
  })

  it('falls back to light without matchMedia and removes its listener on disposal', () => {
    const appearance = start()
    scope.stop()
    expect(remove).toHaveBeenCalledWith('change', listener)
    vi.stubGlobal('matchMedia', undefined)
    expect(start().mode.value).toBe('light')
    appearance.setPreference('light')
  })
})
