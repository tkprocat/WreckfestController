import { describe, expect, it } from 'vitest'
import { APPEARANCE_KEY } from '@/composables/useAppearance'
import { palettes } from './tokens'
import html from '../../index.html?raw'
import css from '../styles.css?raw'

// index.html and styles.css paint the page before Vue runs, so they repeat a few values
// that otherwise live in tokens.ts and useAppearance. These keep the copies in step.
describe('pre-paint theme', () => {
  it('reads the preference useAppearance saves', () => {
    expect(html).toContain(`localStorage.getItem('${APPEARANCE_KEY}')`)
  })

  it.each(['light', 'dark'] as const)('falls back to the %s palette page colors', mode => {
    expect(css).toContain(`--page-background: ${palettes[mode].bodyColor}`)
    expect(css).toContain(`--page-text: ${palettes[mode].textColor1}`)
  })
})
