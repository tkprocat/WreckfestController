import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import StatusBadge from '@/components/StatusBadge.vue'
import RotationPanel, { type RotationState } from './RotationPanel.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))
const hub = vi.hoisted(() => new Map<string, (message: unknown) => void>())
vi.mock('@/realtime/hub', () => ({
  onHub: (event: string, handler: (message: unknown) => void) => {
    hub.set(event, handler)
    return () => hub.delete(event)
  },
}))

const when = '2026-09-30T18:00:00Z'
const loop = (tracks: Record<string, unknown>[], version = 'v1', collectionName = 'Evening') => ({ count: tracks.length, tracks, collectionName, version })
const cup = (extra: Record<string, unknown> = {}) => ({
  id: 5,
  name: 'Friday Derby',
  description: 'Bring a helmet',
  startTime: when,
  timeZone: 'Europe/Copenhagen',
  repeat: { frequency: 'weekly', days: [5], time: '20:00' },
  repeatDescription: 'Weekly',
  serverConfig: { serverName: 'Derby night' },
  sessionMode: '30p-aggr',
  gridOrder: null,
  collectionId: null,
  collectionName: 'Derby set',
  tracks: [{ track: 'arena' }],
  nextOccurrence: null,
  lastOccurrence: null,
  lastOutcome: null,
  isActive: true,
  activatedAt: when,
  createdBy: 'admin',
  createdAt: when,
  updatedAt: when,
  version: 7,
  ...extra,
})

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })
const noContent = () => ({ data: undefined, error: undefined, response: new Response(null, { status: 204 }) })

let current: unknown = null
let rotation = loop([{ track: 'loop', laps: 3, weather: 'rain' }, { track: 'arena' }])

let wrapper: VueWrapper | undefined
/** Every state the panel reports, latest last: what a page holding it collapsed shows. */
let states: RotationState[] = []
async function mountPanel() {
  states = []
  wrapper = mount(
    defineComponent({
      render: () => h(NMessageProvider, () => h(NDialogProvider, () => h(RotationPanel, { onState: (s: RotationState) => states.push(s) }))),
    }),
    { attachTo: document.body },
  )
  await flushPromises()
  return wrapper
}

const body = () => new DOMWrapper(document.body)
const labelled = (label: string) => body().find(`[aria-label="${label}"]`)
const click = async (label: string, within: DOMWrapper<Element> = body()) => {
  await within.findAll('button').find((b) => b.text().trim() === label)!.trigger('click')
  await flushPromises()
}

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
  current = null
  rotation = loop([{ track: 'loop', laps: 3, weather: 'rain' }, { track: 'arena' }])
  api.GET.mockImplementation((path: string) => {
    if (path === '/api/config/tracks') return Promise.resolve(answer(rotation))
    if (path === '/api/cups/current') return Promise.resolve(current ? answer(current) : noContent())
    if (path === '/api/catalogue/variants') return Promise.resolve(answer([]))
    if (path === '/api/collections') return Promise.resolve(answer([{ id: 3, name: 'Short set', version: 2, trackCount: 4, createdAt: when, updatedAt: when }]))
    if (path === '/api/collections/{id}') return Promise.resolve(answer({ id: 3, name: 'Short set', version: 2, tracks: [], createdAt: when, updatedAt: when }))
    throw new Error(`unexpected GET ${path}`)
  })
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('RotationPanel', () => {
  // The Cups page shows these on its collapsed card.
  it('reports its state as it loads, is edited, conflicts and fails', async () => {
    current = cup()
    api.PUT.mockResolvedValue(refused(loop([{ track: 'fields14' }], 'v9', 'Theirs'), 409))
    await mountPanel()

    expect(states[0]!.status).toBe('loading')
    expect(states.at(-1)).toEqual({ status: 'ready', loaded: true, tracks: 2, cupName: 'Friday Derby', dirty: false, notice: null })

    await labelled('Move loop (not in the catalogue) down').trigger('click')
    expect(states.at(-1)).toMatchObject({ dirty: true, notice: null })

    await click('Save rotation')
    expect(states.at(-1)).toMatchObject({ dirty: true, notice: 'conflict' })

    api.GET.mockImplementation(() => Promise.reject(new Error('offline')))
    await click('Keep mine')
    await click('Discard changes')
    // The failed reload keeps the draft, and says so.
    expect(states.at(-1)).toMatchObject({ status: 'failed', loaded: true, dirty: true })
  })

  it('says when no cup set the rotation', async () => {
    await mountPanel()

    expect(wrapper!.text()).toContain("The server's own rotation.")
    expect((labelled('Rotation name').element as HTMLInputElement).value).toBe('Evening')
  })

  it('names the cup that set the rotation', async () => {
    current = cup()
    await mountPanel()

    expect(wrapper!.text()).toContain('Set by Friday Derby')
    expect(wrapper!.findAllComponents(StatusBadge).find((badge) => badge.text() === 'Cup')!.props('tone')).toBe('neutral')
  })

  // The rotation's version guards the file: a save made against it keeps every track setting.
  it('saves with the version it read, keeping each track\'s settings', async () => {
    api.PUT.mockResolvedValue(answer(loop([{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }], 'v2')))
    await mountPanel()

    await labelled('Move loop (not in the catalogue) down').trigger('click')
    await click('Save rotation')

    expect(api.PUT).toHaveBeenCalledWith('/api/config/tracks', {
      body: { collectionName: 'Evening', tracks: [{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }] },
      headers: { 'If-Match': '"v1"' },
    })
    expect(body().text()).toContain('Rotation saved.')
  })

  it('shows what changed when the rotation was changed meanwhile', async () => {
    api.PUT.mockResolvedValue(refused(loop([{ track: 'fields14', laps: 8 }], 'v9', 'Theirs'), 409))
    await mountPanel()

    await labelled('Move loop (not in the catalogue) down').trigger('click')
    await click('Save rotation')

    expect(wrapper!.text()).toContain('Changed elsewhere')
    expect(wrapper!.text()).toContain('fields14 [laps 8]')

    // Keep mine: my draft stays, and the next save is against their version.
    api.PUT.mockResolvedValue(answer(loop([{ track: 'arena' }], 'v10')))
    await click('Keep mine')
    await click('Save rotation')
    expect(api.PUT.mock.calls[1]![1].headers).toEqual({ 'If-Match': '"v9"' })
  })

  it('offers to save to the active cup too, sending the cup back whole', async () => {
    current = cup()
    api.PUT.mockImplementation((path: string) =>
      Promise.resolve(path === '/api/config/tracks' ? answer(loop([{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }], 'v2')) : answer(cup({ version: 8 }))),
    )
    await mountPanel()

    await labelled('Move loop (not in the catalogue) down').trigger('click')
    await click('Save rotation')
    await click('Also save to "Friday Derby"')

    const [path, request] = api.PUT.mock.calls[1]!
    expect(path).toBe('/api/cups/{id}')
    expect(request.headers).toEqual({ 'If-Match': '"7"' })
    expect(request.body).toEqual(
      expect.objectContaining({
        name: 'Friday Derby',
        description: 'Bring a helmet',
        repeat: { frequency: 'weekly', days: [5], time: '20:00' },
        serverConfig: { serverName: 'Derby night' },
        sessionMode: '30p-aggr',
        collectionId: null,
        tracks: [{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }],
      }),
    )
  })

  it('asks before saving to the collection a cup follows', async () => {
    current = cup({ collectionId: 3, collectionName: 'Short set' })
    api.PUT.mockImplementation((path: string) =>
      Promise.resolve(path === '/api/config/tracks' ? answer(loop([{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }], 'v2')) : answer({ id: 3, name: 'Short set', version: 3, tracks: [], createdAt: when, updatedAt: when })),
    )
    await mountPanel()

    await labelled('Move loop (not in the catalogue) down').trigger('click')
    await click('Save rotation')
    await click('Also save to the collection "Short set"')
    expect(body().find('.n-dialog').text()).toContain('changes that collection for everything that uses it')
    await click('Save', body().find('.n-dialog'))

    expect(api.PUT).toHaveBeenLastCalledWith('/api/collections/{id}', {
      params: { path: { id: 3 } },
      body: { name: 'Short set', tracks: [{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }] },
      headers: { 'If-Match': '"2"' },
    })
  })

  it('deploys a collection after asking, then shows the new rotation', async () => {
    api.POST.mockResolvedValue(answer({ message: 'Deployed', collectionName: 'Short set', count: 4 }))
    await mountPanel()
    const vm = wrapper!.findComponent(RotationPanel).vm as unknown as { deployId: number | null }
    vm.deployId = 3
    await flushPromises()
    rotation = loop([{ track: 'short1' }], 'v5', 'Short set')

    await click('Deploy')
    await click('Deploy', body().find('.n-dialog'))

    expect(api.POST).toHaveBeenCalledWith('/api/collections/{id}/deploy', { params: { path: { id: 3 } } })
    expect((labelled('Rotation name').element as HTMLInputElement).value).toBe('Short set')
  })

  // A cup starting rewrites the file: an untouched draft follows it, an edited one is kept.
  it('follows a cup starting, but keeps unsaved edits and says so', async () => {
    await mountPanel()
    rotation = loop([{ track: 'cup_track' }], 'v3', 'Cup set')
    hub.get('CupActivated')!({ cupId: 5, cupName: 'Friday Derby', timestamp: when })
    await flushPromises()
    expect((labelled('Rotation name').element as HTMLInputElement).value).toBe('Cup set')

    await labelled('Rotation name').setValue('Mine')
    rotation = loop([{ track: 'other' }], 'v4', 'Other')
    hub.get('CupActivated')!({ cupId: 5, cupName: 'Friday Derby', timestamp: when })
    await flushPromises()

    expect((labelled('Rotation name').element as HTMLInputElement).value).toBe('Mine')
    expect(wrapper!.text()).toContain('The rotation changed on the server')
  })

  it('shuffles the draft without saving it', async () => {
    rotation = loop(['a', 'b', 'c', 'd', 'e', 'f'].map((track) => ({ track })))
    await mountPanel()
    const random = vi.spyOn(Math, 'random').mockReturnValue(0)

    await click('Shuffle')

    const vm = wrapper!.findComponent(RotationPanel).vm as unknown as { tracks: { track: string }[] }
    expect(vm.tracks.map((t) => t.track)).not.toEqual(['a', 'b', 'c', 'd', 'e', 'f'])
    expect(vm.tracks.map((t) => t.track).sort()).toEqual(['a', 'b', 'c', 'd', 'e', 'f'])
    expect(api.PUT).not.toHaveBeenCalled()
    random.mockRestore()
  })

  function deferred<T>() {
    let resolve!: (value: T) => void
    const promise = new Promise<T>((r) => (resolve = r))
    return { promise, resolve }
  }
  const nameValue = () => (labelled('Rotation name').element as HTMLInputElement).value
  const moveLoopDown = () => labelled('Move loop (not in the catalogue) down').trigger('click')

  // The collection's version is the one read when the panel loaded: a change made to the
  // collection since then is a conflict, not silently replaced.
  it('saves to the followed collection with the version it loaded', async () => {
    current = cup({ collectionId: 3, collectionName: 'Short set' })
    api.PUT.mockImplementation((path: string) =>
      Promise.resolve(path === '/api/config/tracks' ? answer(loop([{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }], 'v2')) : refused({ id: 3, name: 'Short set', version: 3, tracks: [], createdAt: when, updatedAt: when }, 409)),
    )
    await mountPanel()
    const reads = api.GET.mock.calls.filter(([path]) => path === '/api/collections/{id}').length
    // Someone changes the collection after the panel loaded it.
    const others = api.GET.getMockImplementation()!
    api.GET.mockImplementation((path: string) =>
      path === '/api/collections/{id}' ? Promise.resolve(answer({ id: 3, name: 'Short set', version: 3, tracks: [], createdAt: when, updatedAt: when })) : others(path),
    )

    await moveLoopDown()
    await click('Save rotation')
    await click('Also save to the collection "Short set"')
    await click('Save', body().find('.n-dialog'))

    expect(api.PUT.mock.calls.at(-1)![1].headers).toEqual({ 'If-Match': '"2"' })
    expect(api.GET.mock.calls.filter(([path]) => path === '/api/collections/{id}').length).toBeLessThanOrEqual(reads + 1)
    expect(body().text()).toContain('was changed meanwhile')
  })

  // The offer is for what was saved; editing again withdraws it.
  it('withdraws the offer to save to the cup once the draft is edited again', async () => {
    current = cup()
    api.PUT.mockResolvedValue(answer(loop([{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }], 'v2')))
    await mountPanel()

    await moveLoopDown()
    await click('Save rotation')
    expect(body().text()).toContain('Also save to "Friday Derby"')

    await labelled('Rotation name').setValue('Edited after saving')
    await flushPromises()
    expect(body().text()).not.toContain('Also save to "Friday Derby"')
  })

  // A reload on its way when the user types keeps what was typed.
  it('keeps typing done while a reload is on its way', async () => {
    await mountPanel()
    const slow = deferred<unknown>()
    api.GET.mockImplementation((path: string) => (path === '/api/config/tracks' ? slow.promise : Promise.resolve(answer(path === '/api/cups/current' ? undefined : []))))

    await click('Reload')
    await labelled('Rotation name').setValue('Typed meanwhile')
    slow.resolve(answer(loop([{ track: 'other' }], 'v7', 'From the file')))
    await flushPromises()

    expect(nameValue()).toBe('Typed meanwhile')
  })

  // A hub reload that started before a save must not put the older rotation back.
  it('ignores a reload that started before a save', async () => {
    await mountPanel()
    const old = deferred<unknown>()
    api.GET.mockImplementation((path: string) => (path === '/api/config/tracks' ? old.promise : Promise.resolve(path === '/api/cups/current' ? noContent() : answer([]))))
    hub.get('CupActivated')!({ cupId: 5, cupName: 'Friday Derby', timestamp: when })
    await flushPromises()

    api.PUT.mockResolvedValue(answer(loop([{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }], 'v2', 'Saved name')))
    await labelled('Rotation name').setValue('Saved name')
    await click('Save rotation')
    old.resolve(answer(loop([{ track: 'stale' }], 'v0', 'Stale')))
    await flushPromises()

    expect(nameValue()).toBe('Saved name')
  })

  it('locks the name while a save is on its way', async () => {
    const pending = deferred<unknown>()
    api.PUT.mockReturnValue(pending.promise)
    await mountPanel()

    await moveLoopDown()
    await click('Save rotation')
    expect(labelled('Rotation name').attributes('disabled')).toBeDefined()

    pending.resolve(answer(loop([{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }], 'v2')))
    await flushPromises()
    expect(labelled('Rotation name').attributes('disabled')).toBeUndefined()
  })

  // The API requires the name: the panel says so instead of sending a save it refuses.
  it('needs a name before it saves', async () => {
    rotation = loop([{ track: 'loop' }, { track: 'arena' }], 'v1', '')
    await mountPanel()

    await moveLoopDown()
    const save = wrapper!.findAll('button').find((b) => b.text() === 'Save rotation')!
    expect(save.attributes('disabled')).toBeDefined()
    expect(labelled('Rotation name').attributes('aria-invalid')).toBe('true')
    expect(wrapper!.text()).toContain('A name is needed')

    await labelled('Rotation name').setValue('Named')
    expect(save.attributes('disabled')).toBeUndefined()
  })

  it('moves focus into the conflict view, and back to Save after it', async () => {
    api.PUT.mockResolvedValue(refused(loop([{ track: 'fields14' }], 'v9', 'Theirs'), 409))
    await mountPanel()

    await moveLoopDown()
    await click('Save rotation')
    await flushPromises()
    expect(document.activeElement?.closest('[aria-label="Changed elsewhere"]')).not.toBeNull()

    await click('Keep mine')
    await flushPromises()
    expect(document.activeElement?.hasAttribute('data-save')).toBe(true)
  })

  // Two loads, answered in reverse order: only the latest decides the cup, and the
  // collection a save to it writes.
  it('keeps the latest load\'s collection when an older one answers late', async () => {
    current = cup({ collectionId: 3, collectionName: 'Short set' })
    await mountPanel()
    const answers: ((value: unknown) => void)[] = []
    const others = api.GET.getMockImplementation()!
    api.GET.mockImplementation((path: string) => {
      if (path === '/api/collections/{id}') return new Promise((resolve) => answers.push(resolve))
      if (path === '/api/cups/current') return Promise.resolve(answer(cup({ collectionId: answers.length === 0 ? 3 : 4, collectionName: 'Other' })))
      return others(path)
    })

    hub.get('CupActivated')!({ cupId: 5, cupName: 'Friday Derby', timestamp: when })
    await flushPromises()
    hub.get('CupActivated')!({ cupId: 5, cupName: 'Friday Derby', timestamp: when })
    await flushPromises()
    answers[1]!(answer({ id: 4, name: 'Other', version: 9, tracks: [], createdAt: when, updatedAt: when }))
    await flushPromises()
    answers[0]!(answer({ id: 3, name: 'Short set', version: 2, tracks: [], createdAt: when, updatedAt: when }))
    await flushPromises()

    api.PUT.mockImplementation((path: string) =>
      Promise.resolve(path === '/api/config/tracks' ? answer(loop([{ track: 'arena' }, { track: 'loop', laps: 3, weather: 'rain' }], 'v2')) : answer({ id: 4, name: 'Other', version: 10, tracks: [], createdAt: when, updatedAt: when })),
    )
    await moveLoopDown()
    await click('Save rotation')
    await click('Also save to the collection "Other"')
    await click('Save', body().find('.n-dialog'))

    expect(api.PUT).toHaveBeenLastCalledWith('/api/collections/{id}', expect.objectContaining({ params: { path: { id: 4 } }, headers: { 'If-Match': '"9"' } }))
  })

  // A reload that starts while the save is on its way read the file before it.
  it('ignores a reload that started during a save', async () => {
    const put = deferred<unknown>()
    api.PUT.mockReturnValue(put.promise)
    await mountPanel()
    await labelled('Rotation name').setValue('Saved name')
    await click('Save rotation')

    const old = deferred<unknown>()
    const others = api.GET.getMockImplementation()!
    api.GET.mockImplementation((path: string) => (path === '/api/config/tracks' ? old.promise : others(path)))
    hub.get('CupActivated')!({ cupId: 5, cupName: 'Friday Derby', timestamp: when })
    await flushPromises()

    put.resolve(answer(loop([{ track: 'loop', laps: 3, weather: 'rain' }, { track: 'arena' }], 'v2', 'Saved name')))
    await flushPromises()
    old.resolve(answer(loop([{ track: 'stale' }], 'v1', 'Evening')))
    await flushPromises()

    expect(nameValue()).toBe('Saved name')
  })

  // After "Use theirs" the draft is clean and Save disabled: focus goes to the name.
  it('puts focus on the name after using theirs', async () => {
    api.PUT.mockResolvedValue(refused(loop([{ track: 'fields14' }], 'v9', 'Theirs'), 409))
    await mountPanel()

    await moveLoopDown()
    await click('Save rotation')
    await click('Use theirs')
    await flushPromises()

    expect(document.activeElement?.getAttribute('aria-label')).toBe('Rotation name')
    expect(nameValue()).toBe('Theirs')
  })
})
