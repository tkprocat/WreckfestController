import { describe, expect, it } from 'vitest'
import { textOn } from './color'

describe('textOn', () => {
  it.each([
    ['#ffffff', '#000000'],
    ['#000000', '#ffffff'],
    // Mid grey: black reads at 5.3:1, white at only 3.9:1.
    ['#808080', '#000000'],
    ['#3366ff', '#ffffff'],
    ['#ffcc00', '#000000'],
    ['#aa5500', '#ffffff'],
    // Either side of where the two ratios meet (luminance ~0.179).
    ['#747474', '#ffffff'],
    ['#777777', '#000000'],
  ])('%s gets %s', (background, text) => {
    expect(textOn(background)).toBe(text)
  })
})
