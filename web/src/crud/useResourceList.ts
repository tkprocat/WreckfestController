import { ref, shallowRef } from 'vue'
import { problemMessage } from '@/api/problems'

type Key = string | number

/**
 * A page's list of rows from the API, kept honest about what it knows.
 *
 * - Loads are numbered: only the latest load's answer is applied, so an older one that
 *   arrives late cannot put back rows that have since changed.
 * - A row added, changed or removed on this page while a load is on its way stays so when
 *   that load answers: the load was asked before the change.
 * - A load that fails keeps what is shown and says why.
 */
export function useResourceList<T extends { id: Key }>(
  load: () => Promise<{ data?: T[]; error?: unknown }>,
  what: string,
) {
  const items = shallowRef<T[]>([])
  const loading = ref(false)
  /** Whether a load has answered at least once: adding waits for it. */
  const loaded = ref(false)
  const error = ref<string | null>(null)

  let started = 0
  // Changes made since the running load started: a row, or null for a removal.
  let since = new Map<Key, T | null>()

  async function reload(): Promise<void> {
    const id = ++started
    since = new Map()
    loading.value = true
    try {
      const { data, error: problem } = await load()
      if (id !== started) {
        return
      }

      if (!data) {
        error.value = problemMessage(problem, `The ${what} could not be loaded.`)
        return
      }

      let rows = data
      for (const [key, row] of since) {
        rows = row === null ? rows.filter((r) => r.id !== key) : upsert(rows, row)
      }

      items.value = rows
      error.value = null
      loaded.value = true
    } catch {
      if (id === started) {
        error.value = `The ${what} could not be loaded: no answer from the controller.`
      }
    } finally {
      if (id === started) {
        loading.value = false
      }
    }
  }

  function upsert(rows: T[], row: T): T[] {
    return rows.some((r) => r.id === row.id) ? rows.map((r) => (r.id === row.id ? row : r)) : [...rows, row]
  }

  /** A row the server answered with: changed in place, or added. */
  function replace(row: T) {
    items.value = upsert(items.value, row)
    if (loading.value) {
      since.set(row.id, row)
    }
  }

  function remove(key: Key) {
    items.value = items.value.filter((r) => r.id !== key)
    if (loading.value) {
      since.set(key, null)
    }
  }

  return { items, loading, loaded, error, reload, replace, remove }
}
