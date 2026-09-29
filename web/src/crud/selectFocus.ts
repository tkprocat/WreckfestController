import type { Directive } from 'vue'

type Attrs = Record<string, string | undefined>

/**
 * Names a Naive UI select where keyboard focus lands. NSelect passes `input-props` to its
 * search input, but that input has tabindex -1: the tab stop is an unnamed div. This copies
 * the same attributes (aria-label, aria-describedby, aria-invalid) onto the element that
 * takes focus, as a combobox, and again after each render in case Naive replaced it.
 *
 * Use it beside `input-props`: `<NSelect v-select-focus="inputProps" :input-props="inputProps" />`.
 */
export const vSelectFocus: Directive<HTMLElement, Attrs> = {
  mounted: apply,
  updated: apply,
}

function apply(root: HTMLElement, binding: { value: Attrs }) {
  const target = root.querySelector<HTMLElement>('[tabindex="0"]')
  if (!target) {
    return
  }

  target.setAttribute('role', 'combobox')
  for (const [name, value] of Object.entries(binding.value ?? {})) {
    if (value === undefined) {
      target.removeAttribute(name)
    } else {
      target.setAttribute(name, value)
    }
  }
}
