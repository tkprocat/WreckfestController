import { computed, reactive, ref, shallowRef, type UnwrapRef } from 'vue'
import { useMessage } from 'naive-ui'
import { NO_ANSWER, type Outcome } from './outcome'

/**
 * Editing one row - a new one, or an existing one - in a form.
 *
 * - The form edits a copy (the draft); `changes` is what differs from the row it came from.
 * - Save sends the draft with the row's version (for If-Match, when the resource has one).
 * - The form is locked while saving: the answer replaces it, so an edit made meanwhile
 *   would be lost.
 * - A version conflict keeps both: `conflict` holds the row as it is now, and the page asks
 *   whether to use theirs or keep mine (mine rebased onto their version, saved again).
 */
export function useResourceEditor<Row extends object, Draft extends object>(options: {
  /** The form's starting values: from a row, or for a new one (null). */
  toDraft: (row: Row | null) => Draft
  /** Sends the draft; `version` is the one it was edited from (undefined for a new row). */
  save: (draft: Draft, version: number | undefined, row: Row | null) => Promise<Outcome<Row>>
  /** Called with the saved row; the editor closes. */
  saved: (row: Row, isNew: boolean) => void
  /** What the toast calls it: "tag", "collection". */
  what: string
}) {
  const message = useMessage()

  const open = ref(false)
  const editing = shallowRef<Row | null>(null)
  const draft = reactive(options.toDraft(null)) as UnwrapRef<Draft>
  const base = shallowRef<Draft>(options.toDraft(null))
  const version = ref<number | undefined>()
  const saving = ref(false)
  const errors = ref<Record<string, string>>({})
  const conflict = shallowRef<{ mine: Draft; theirs: Row } | null>(null)

  const isNew = computed(() => editing.value === null)

  /** The fields whose values differ from the row the draft came from. */
  const changes = computed(() =>
    (Object.keys(draft as object) as (keyof Draft)[]).filter(
      (key) => JSON.stringify((draft as Draft)[key]) !== JSON.stringify(base.value[key]),
    ),
  )

  function start(row: Row | null) {
    editing.value = row
    const values = options.toDraft(row)
    base.value = options.toDraft(row)
    Object.assign(draft as object, values)
    version.value = versionOf(row)
    errors.value = {}
    conflict.value = null
    open.value = true
  }

  function close() {
    if (!saving.value) {
      open.value = false
      conflict.value = null
    }
  }

  async function save(): Promise<void> {
    if (saving.value) {
      return
    }

    saving.value = true
    errors.value = {}
    try {
      const outcome = await options.save({ ...(draft as Draft) }, version.value, editing.value)
      switch (outcome.kind) {
        case 'ok':
          if (outcome.row) {
            options.saved(outcome.row, isNew.value)
          }

          message.success(`${capitalise(options.what)} saved.`)
          open.value = false
          break
        case 'invalid':
          errors.value = outcome.errors
          message.error(outcome.message)
          break
        case 'conflict':
          conflict.value = { mine: { ...(draft as Draft) }, theirs: outcome.current }
          break
        case 'refused':
          message.error(outcome.message)
          break
        case 'no-answer':
          message.error(NO_ANSWER)
          break
      }
    } finally {
      saving.value = false
    }
  }

  /** Someone else's change wins: the form shows theirs, and mine is dropped. */
  function useTheirs() {
    const theirs = conflict.value?.theirs
    if (!theirs) {
      return
    }

    options.saved(theirs, false)
    editing.value = theirs
    Object.assign(draft as object, options.toDraft(theirs))
    base.value = options.toDraft(theirs)
    version.value = versionOf(theirs)
    conflict.value = null
  }

  /**
   * Mine wins: the form keeps my values on top of their version, to be saved again. The
   * list shows theirs meanwhile - it is what the server has.
   */
  function keepMine() {
    const theirs = conflict.value?.theirs
    if (!theirs) {
      return
    }

    options.saved(theirs, false)
    editing.value = theirs
    base.value = options.toDraft(theirs)
    version.value = versionOf(theirs)
    conflict.value = null
  }

  return { open, editing, isNew, draft, changes, saving, errors, conflict, start, close, save, useTheirs, keepMine }
}

/** A row's version, for If-Match; undefined for a resource without versions (tags). */
function versionOf(row: object | null): number | undefined {
  const version = (row as { version?: unknown } | null)?.version
  return typeof version === 'number' ? version : undefined
}

function capitalise(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1)
}
