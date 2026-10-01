import type { GlobalThemeOverrides } from 'naive-ui'

export type ColorMode = 'light' | 'dark'

const shared = {
  fontFamily: "system-ui, -apple-system, 'Segoe UI', sans-serif",
  fontFamilyMono: "'Cascadia Mono', Consolas, monospace",
  fontSize: '14px',
  fontWeightStrong: '600',
  borderRadius: '8px',
  borderRadiusSmall: '5px',
}

// The provider and our CSS variables use these same semantic colors. Keep status
// colors separate from the warm brand accent, especially on destructive controls.
export const palettes = {
  light: {
    bodyColor: '#f4f5f7', cardColor: '#ffffff', modalColor: '#ffffff', popoverColor: '#ffffff',
    tableColor: '#ffffff', tableHeaderColor: '#f0f2f5', inputColor: '#ffffff',
    textColor1: '#202631', textColor2: '#414b5b', textColor3: '#616d7d',
    borderColor: '#d9dee6', dividerColor: '#e5e8ed',
    primaryColor: '#b84b16', primaryColorHover: '#a23e10', primaryColorPressed: '#88330b', primaryColorSuppl: '#b84b16',
    errorColor: '#bd3046', errorColorHover: '#a92439', errorColorPressed: '#912033', errorColorSuppl: '#bd3046',
  },
  dark: {
    bodyColor: '#11151b', cardColor: '#1a2029', modalColor: '#1e2530', popoverColor: '#242c38',
    tableColor: '#1a2029', tableHeaderColor: '#242c38', inputColor: '#202834',
    textColor1: '#edf0f5', textColor2: '#c3cad5', textColor3: '#9aa6b7',
    borderColor: '#3b4758', dividerColor: '#303a48',
    primaryColor: '#f7a66b', primaryColorHover: '#ffbb89', primaryColorPressed: '#e69153', primaryColorSuppl: '#f7a66b',
    errorColor: '#ff8b9b', errorColorHover: '#ffa3b0', errorColorPressed: '#ed7386', errorColorSuppl: '#ff8b9b',
  },
} satisfies Record<ColorMode, NonNullable<GlobalThemeOverrides['common']>>

export function themeOverrides(mode: ColorMode): GlobalThemeOverrides {
  return {
    common: { ...shared, ...palettes[mode] },
    Card: { borderRadius: '12px' },
  }
}

/** Applied to the document so teleported overlays and native inputs inherit it. */
export function themeVariables(mode: ColorMode): Record<string, string> {
  const p = palettes[mode]
  return {
    '--page-background': p.bodyColor,
    '--page-text': p.textColor1,
    '--text-secondary': p.textColor2,
    '--text-muted': p.textColor3,
    '--surface': p.cardColor,
    '--header-background': p.cardColor,
    '--header-border': p.dividerColor,
    '--border-color': p.borderColor,
    '--accent': p.primaryColor,
    '--error-color': p.errorColor,
    '--font-family': shared.fontFamily,
  }
}
