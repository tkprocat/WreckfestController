import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NMessageProvider } from 'naive-ui'
import type { Outcome } from './outcome'
import { useResourceEditor } from './useResourceEditor'

interface Row {
  id: number
  name: string
  version: number
}

interface Draft {
  name: string
}

let wrapper: VueWrapper | undefined

/** The editor needs Naive UI's message context: run it inside a mounted component. */
function editor(save: (draft: Draft, version: number | undefined) => Promise<Outcome<Row>>) {
  const saved = vi.fn()
  let result!: ReturnType<typeof useResourceEditor<Row, Draft>>
  const Inner = defineComponent({
    setup() {
      result = useResourceEditor<Row, Draft>({ what: 'row', toDraft: (row) => ({ name: row?.name ?? '' }), save, saved })
      return () => h('div')
    },
  })
  wrapper = mount(defineComponent({ render: () => h(NMessageProvider, () => h(Inner)) }), { attachTo: document.body })
  return { editor: result, saved }
}

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

const row = (name: string, version: number): Row => ({ id: 1, name, version })

describe('useResourceEditor', () => {
  it('saves the draft with the version it was edited from, then closes', async () => {
    const save = vi.fn(async () => ({ kind: 'ok' as const, row: row('New', 4) }))
    const { editor: e, saved } = editor(save)

    e.start(row('Old', 3))
    e.draft.name = 'New'
    expect(e.changes.value).toEqual(['name'])
    await e.save()

    expect(save).toHaveBeenCalledWith({ name: 'New' }, 3, row('Old', 3))
    expect(saved).toHaveBeenCalledWith(row('New', 4), false)
    expect(e.open.value).toBe(false)
  })

  // The answer replaces the form: an edit made meanwhile would be lost.
  it('is locked while saving, and a second save is ignored', async () => {
    let finish!: (outcome: Outcome<Row>) => void
    const save = vi.fn(() => new Promise<Outcome<Row>>((resolve) => (finish = resolve)))
    const { editor: e } = editor(save)
    e.start(null)

    const first = e.save()
    void e.save()
    expect(e.saving.value).toBe(true)
    finish({ kind: 'ok', row: row('A', 1) })
    await first

    expect(save).toHaveBeenCalledTimes(1)
    expect(e.saving.value).toBe(false)
  })

  // The answer belongs to the row being saved: it must not close or file a different one.
  it('ignores a start while saving', async () => {
    let finish!: (outcome: Outcome<Row>) => void
    const save = vi.fn(() => new Promise<Outcome<Row>>((resolve) => (finish = resolve)))
    const { editor: e, saved } = editor(save)
    e.start(row('A', 1))

    const saving = e.save()
    e.start(null)
    expect(e.editing.value).toEqual(row('A', 1))
    finish({ kind: 'ok', row: row('A', 2) })
    await saving

    expect(saved).toHaveBeenCalledWith(row('A', 2), false)
  })

  it('keeps the form open with the field messages', async () => {
    const { editor: e } = editor(async () => ({ kind: 'invalid', errors: { name: 'Required.' }, message: 'Required.' }))
    e.start(null)

    await e.save()

    expect(e.errors.value).toEqual({ name: 'Required.' })
    expect(e.open.value).toBe(true)
  })

  describe('a version conflict', () => {
    async function conflicted() {
      let attempt = 0
      const save = vi.fn(async (): Promise<Outcome<Row>> =>
        attempt++ === 0 ? { kind: 'conflict', current: row('Theirs', 5) } : { kind: 'ok', row: row('Mine', 6) },
      )
      const setup = editor(save)
      setup.editor.start(row('Original', 4))
      setup.editor.draft.name = 'Mine'
      await setup.editor.save()
      return { ...setup, save }
    }

    it('keeps both, and saves nothing yet', async () => {
      const { editor: e, saved } = await conflicted()

      expect(e.conflict.value).toEqual({ mine: { name: 'Mine' }, theirs: row('Theirs', 5) })
      expect(e.open.value).toBe(true)
      expect(saved).not.toHaveBeenCalled()
    })

    it('"Use theirs" shows theirs and drops mine', async () => {
      const { editor: e, saved } = await conflicted()

      e.useTheirs()

      expect(e.draft.name).toBe('Theirs')
      expect(e.conflict.value).toBeNull()
      expect(saved).toHaveBeenCalledWith(row('Theirs', 5), false)
    })

    // Mine on top of their version: the next save sends If-Match 5, not the stale 4.
    it('"Keep mine" keeps my values against their version', async () => {
      const { editor: e, save } = await conflicted()

      e.keepMine()
      expect(e.draft.name).toBe('Mine')
      await e.save()
      await flushPromises()

      expect(save).toHaveBeenLastCalledWith({ name: 'Mine' }, 5, row('Theirs', 5))
    })
  })
})
