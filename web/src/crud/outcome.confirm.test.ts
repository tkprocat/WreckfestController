import { afterEach, describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider } from 'naive-ui'
import { useConfirm } from './outcome'

let wrapper: VueWrapper | undefined

function confirmer() {
  let confirm!: ReturnType<typeof useConfirm>
  const Inner = defineComponent({
    setup() {
      confirm = useConfirm()
      return () => h('div')
    },
  })
  wrapper = mount(defineComponent({ render: () => h(NDialogProvider, () => h(Inner)) }), { attachTo: document.body })
  return confirm
}

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

const options = { title: 'Delete tag', content: 'This cannot be undone.', positive: 'Delete' }

describe('useConfirm', () => {
  // Naive UI names nothing: the dialog is tied to its title and content by id.
  it('names the dialog by its title, and describes it by its content', async () => {
    void confirmer()(options)
    await flushPromises()

    const dialog = document.querySelector('[role="dialog"]')!
    expect(document.getElementById(dialog.getAttribute('aria-labelledby')!)!.textContent).toBe('Delete tag')
    expect(document.getElementById(dialog.getAttribute('aria-describedby')!)!.textContent).toBe('This cannot be undone.')
  })

  // A screen reader announces the dialog when focus enters it: the name must be there first.
  // (The focus trap's own aria-hidden sentinel, outside the dialog, may take focus before.)
  it('is named before focus enters it, and focuses Cancel', async () => {
    const entered: { label: string; named: boolean }[] = []
    const record = (event: FocusEvent) => {
      const dialog = (event.target as Element).closest('[role="dialog"]')
      if (dialog) {
        entered.push({ label: (event.target as Element).textContent!.trim(), named: dialog.hasAttribute('aria-labelledby') })
      }
    }
    document.addEventListener('focusin', record)
    void confirmer()(options)
    await flushPromises()
    document.removeEventListener('focusin', record)

    expect(entered).toEqual([{ label: 'Cancel', named: true }])
  })

  it('resolves true when confirmed', async () => {
    const answer = confirmer()(options)
    await flushPromises()

    ;[...document.querySelectorAll<HTMLButtonElement>('.n-dialog button')].find((b) => b.textContent!.trim() === 'Delete')!.click()

    await expect(answer).resolves.toBe(true)
  })

  // Escape closes the dialog: the question must settle as "no", not stay pending.
  it('resolves false on Escape', async () => {
    const answer = confirmer()(options)
    await flushPromises()

    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }))

    await expect(answer).resolves.toBe(false)
  })
})
