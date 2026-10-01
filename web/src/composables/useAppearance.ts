import { computed, onScopeDispose, ref, watchEffect } from 'vue'
import { darkTheme } from 'naive-ui'
import { themeOverrides, themeVariables } from '@/theme/tokens'

export type Appearance = 'system' | 'light' | 'dark'
export const APPEARANCE_KEY = 'wreckfest.appearance'

function valid(value: unknown): value is Appearance {
  return value === 'system' || value === 'light' || value === 'dark'
}

function savedAppearance(): Appearance {
  try {
    const value = localStorage.getItem(APPEARANCE_KEY)
    return valid(value) ? value : 'system'
  } catch {
    return 'system'
  }
}

/** One owner in App: OS preference, Naive UI, native controls and page CSS agree. */
export function useAppearance() {
  const preference = ref<Appearance>(savedAppearance())
  const media = typeof window.matchMedia === 'function' ? window.matchMedia('(prefers-color-scheme: dark)') : null
  const osDark = ref(media?.matches ?? false)
  const onChange = (event: MediaQueryListEvent) => { osDark.value = event.matches }
  media?.addEventListener('change', onChange)
  onScopeDispose(() => media?.removeEventListener('change', onChange))

  const mode = computed(() => preference.value === 'system' ? (osDark.value ? 'dark' : 'light') : preference.value)
  const theme = computed(() => mode.value === 'dark' ? darkTheme : null)
  const overrides = computed(() => themeOverrides(mode.value))

  watchEffect(() => {
    const root = document.documentElement
    root.dataset.theme = mode.value
    root.style.colorScheme = mode.value
    for (const [name, value] of Object.entries(themeVariables(mode.value))) root.style.setProperty(name, value)
  })

  function setPreference(value: unknown) {
    if (!valid(value)) return
    preference.value = value
    try {
      localStorage.setItem(APPEARANCE_KEY, value)
    } catch {
      // A privacy policy or full storage must not stop a theme changing this session.
    }
  }

  return { preference, mode, theme, overrides, setPreference }
}
