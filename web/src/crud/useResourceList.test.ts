import { describe, expect, it } from 'vitest'
import { useResourceList } from './useResourceList'

interface Row {
  id: number
  name: string
}

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((res) => (resolve = res))
  return { promise, resolve }
}

const rows = (...names: string[]): Row[] => names.map((name, i) => ({ id: i + 1, name }))

describe('useResourceList', () => {
  it('shows what a load answered, and remembers that it has loaded', async () => {
    const list = useResourceList<Row>(async () => ({ data: rows('a', 'b') }), 'rows')
    expect(list.loaded.value).toBe(false)

    await list.reload()

    expect(list.items.value.map((r) => r.name)).toEqual(['a', 'b'])
    expect(list.loaded.value).toBe(true)
  })

  // A later load was asked for because something changed: the earlier answer is older.
  it('applies only the latest load', async () => {
    const first = deferred<{ data: Row[] }>()
    const second = deferred<{ data: Row[] }>()
    const answers = [first.promise, second.promise]
    const list = useResourceList<Row>(() => answers.shift()!, 'rows')

    const one = list.reload()
    const two = list.reload()
    second.resolve({ data: rows('new') })
    await two
    first.resolve({ data: rows('old') })
    await one

    expect(list.items.value.map((r) => r.name)).toEqual(['new'])
  })

  // A row added, changed or removed while a load was on its way stays so: the load was
  // asked before the change. (The #159 "Add account" bug.)
  it('keeps changes made while a load was on its way', async () => {
    const load = deferred<{ data: Row[] }>()
    const list = useResourceList<Row>(() => load.promise, 'rows')

    const loading = list.reload()
    list.replace({ id: 9, name: 'added meanwhile' })
    list.replace({ id: 1, name: 'renamed meanwhile' })
    list.remove(2)
    load.resolve({ data: rows('a', 'b', 'c') })
    await loading

    expect(list.items.value.map((r) => r.name)).toEqual(['renamed meanwhile', 'c', 'added meanwhile'])
  })

  it('keeps what is shown when a load fails, and says why', async () => {
    let fail = false
    const list = useResourceList<Row>(async () => {
      if (fail) {
        throw new TypeError('Failed to fetch')
      }

      return { data: rows('a') }
    }, 'rows')
    await list.reload()

    fail = true
    await list.reload()

    expect(list.items.value.map((r) => r.name)).toEqual(['a'])
    expect(list.error.value).toBe('The rows could not be loaded: no answer from the controller.')
    expect(list.loading.value).toBe(false)
  })
})
