import { afterEach, describe, expect, it } from 'vitest'
import { h, nextTick, ref, withDirectives } from 'vue'
import { mount, type VueWrapper } from '@vue/test-utils'
import { NSelect } from 'naive-ui'
import { vSelectFocus } from './selectFocus'

let wrapper: VueWrapper | undefined
afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

const options = [
  { value: 'a', label: 'A' },
  { value: 'b', label: 'B' },
]

describe('vSelectFocus', () => {
  // Without it, Tab lands on a div with no name: the labelled input is tabindex -1.
  it.each([{ filterable: true }, { filterable: true, multiple: true }, {}])('names the tab stop of a select %o', async (props) => {
    const attrs = ref<Record<string, string | undefined>>({ 'aria-label': 'Origin', 'aria-invalid': 'true' })
    wrapper = mount(
      () =>
        withDirectives(h(NSelect, { ...props, options, value: props.multiple ? ['a'] : 'a', inputProps: attrs.value }), [[vSelectFocus, attrs.value]]),
      { attachTo: document.body },
    )

    const stop = () => wrapper!.element.querySelector('[tabindex="0"]')!
    expect(stop().getAttribute('aria-label')).toBe('Origin')
    expect(stop().getAttribute('role')).toBe('combobox')
    expect(stop().getAttribute('aria-invalid')).toBe('true')

    attrs.value = { 'aria-label': 'Origin', 'aria-invalid': undefined }
    await nextTick()
    expect(stop().hasAttribute('aria-invalid')).toBe(false)
  })
})
