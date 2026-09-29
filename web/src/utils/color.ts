/** WCAG relative luminance of a #RRGGBB colour. */
function luminance(hex: string): number {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4
  })
  return 0.2126 * r! + 0.7152 * g! + 0.0722 * b!
}

/** Black or white text on a #RRGGBB background: whichever has the higher contrast ratio. */
export function textOn(hex: string): '#000000' | '#ffffff' {
  const l = luminance(hex)
  const onBlack = (l + 0.05) / 0.05
  const onWhite = 1.05 / (l + 0.05)
  return onBlack >= onWhite ? '#000000' : '#ffffff'
}
