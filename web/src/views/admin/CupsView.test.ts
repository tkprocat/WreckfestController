import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import CupsView from './CupsView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))
const hub = vi.hoisted(() => new Map<string, (message: unknown) => void>())
vi.mock('@/realtime/hub', () => ({
  onHub: (event: string, handler: (message: unknown) => void) => {
    hub.set(event, handler)
    return () => hub.delete(event)
  },
}))

const when = '2026-10-02T18:00:00Z'
const cup = (id: number, name: string, extra: Record<string, unknown> = {}) => ({
  id,
  name,
  description: '',
  startTime: when,
  timeZone: 'Europe/Copenhagen',
  repeat: null,
  repeatDescription: 'Once',
  serverConfig: null,
  sessionMode: null,
  gridOrder: null,
  collectionId: null,
  collectionName: '',
  tracks: [],
  nextOccurrence: when,
  lastOccurrence: null,
  lastOutcome: null,
  isActive: false,
  activatedAt: null,
  createdBy: 'admin',
  createdAt: when,
  updatedAt: when,
  version: 3,
  ...extra,
})
const friday = () =>
  cup(1, 'Friday Derby', {
    repeat: { frequency: 'weekly', days: [5], time: '20:00' },
    repeatDescription: 'Weekly on Friday at 20:00',
    collectionId: 7,
    collectionName: 'Evening',
    tracks: [{ track: 'loop', laps: 3 }],
    sessionMode: '30p-aggr',
    serverConfig: { serverName: 'Derby night', bots: 4 },
  })
const sunday = () => cup(2, 'Sunday Race', { tracks: [{ track: 'arena', gamemode: 'derby', weather: 'rain' }], collectionName: 'Sunday set', isActive: true })

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

async function mountPage() {
  wrapper = mount(
    defineComponent({ render: () => h(NMessageProvider, () => h(NDialogProvider, () => h(CupsView))) }),
    { attachTo: document.body },
  )
  await flushPromises()
  return wrapper
}

const body = () => new DOMWrapper(document.body)
const labelled = (label: string) => body().find(`[aria-label="${label}"]`)
const dialog = () => body().find('.n-modal')
const confirmDialog = () => body().find('.n-dialog')
const click = async (label: string, within: DOMWrapper<Element> = body()) => {
  await within.findAll('button').find((b) => b.text().trim() === label)!.trigger('click')
  await flushPromises()
}
const editor = () => wrapper!.findComponent({ name: 'CupEditor' }).vm as unknown as { draft: Record<string, unknown> }

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
  api.GET.mockImplementation((path: string) => {
    if (path === '/api/cups') return Promise.resolve(answer({ count: 2, cups: [friday(), sunday()] }))
    if (path === '/api/collections') return Promise.resolve(answer([{ id: 7, name: 'Evening', version: 1, trackCount: 1, createdAt: when, updatedAt: when }]))
    if (path === '/api/catalogue/variants') return Promise.resolve(answer([]))
    throw new Error(`unexpected GET ${path}`)
  })
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('CupsView', () => {
  it('lists cups with their schedule and rotation, and marks the active one', async () => {
    await mountPage()

    expect(wrapper!.text()).toContain('Weekly on Friday at 20:00')
    expect(wrapper!.text()).toContain('Collection: Evening')
    expect(wrapper!.text()).toContain('1 tracks of its own')
    expect(labelled('Activate Sunday Race').attributes('disabled')).toBeDefined()
  })

  // A linked cup sends its collection, not the collection's tracks; everything else as it was.
  it('edits a cup with its version, keeping what the form did not change', async () => {
    api.PUT.mockResolvedValue(answer({ ...friday(), name: 'Friday Night Derby', version: 4 }))
    await mountPage()

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()
    await dialog().find('input[aria-label="Name"]').setValue('Friday Night Derby')
    await click('Save', dialog())

    expect(api.PUT).toHaveBeenCalledWith('/api/cups/{id}', {
      params: { path: { id: 1 } },
      headers: { 'If-Match': '"3"' },
      body: expect.objectContaining({
        name: 'Friday Night Derby',
        startTime: when,
        timeZone: 'Europe/Copenhagen',
        repeat: { frequency: 'weekly', days: [5], time: '20:00' },
        collectionId: 7,
        tracks: null,
        sessionMode: '30p-aggr',
        serverConfig: expect.objectContaining({ serverName: 'Derby night', bots: 4, password: null }),
      }),
    })
    expect(wrapper!.text()).toContain('Friday Night Derby')
  })

  it('keeps a cup\'s own tracks and their unshown settings', async () => {
    api.PUT.mockResolvedValue(answer({ ...sunday(), version: 4 }))
    await mountPage()

    await labelled('Edit Sunday Race').trigger('click')
    await flushPromises()
    await click('Save', dialog())

    const sent = api.PUT.mock.calls[0]![1].body
    expect(sent.collectionId).toBeNull()
    expect(sent.collectionName).toBe('Sunday set')
    expect(sent.tracks).toEqual([expect.objectContaining({ track: 'arena', gamemode: 'derby', weather: 'rain' })])
    expect(sent.repeat).toBeNull()
    expect(sent.serverConfig).toBeNull()
  })

  it('adds a cup that leaves the server\'s rotation alone', async () => {
    api.POST.mockResolvedValue(answer(cup(9, 'One-off'), 201))
    await mountPage()

    await click('Add cup')
    await dialog().find('input[aria-label="Name"]').setValue('One-off')
    await dialog().find('input[aria-label="Starts (your local time)"]').setValue('2026-10-02T20:00')
    editor().draft.source = 'server'
    await click('Add', dialog())

    const sent = api.POST.mock.calls[0]![1].body
    expect(sent.name).toBe('One-off')
    expect(sent.startTime).toBe(new Date('2026-10-02T20:00').toISOString())
    expect(sent.collectionId).toBeNull()
    expect(sent.tracks).toBeNull()
    expect(wrapper!.text()).toContain('One-off')
  })

  it('shows the server\'s message on its field', async () => {
    api.POST.mockResolvedValue(refused({ title: 'One or more validation errors occurred.', status: 400, errors: { startTime: ['startTime is required.'] } }, 400))
    await mountPage()

    await click('Add cup')
    await dialog().find('input[aria-label="Name"]').setValue('No time')
    await click('Add', dialog())

    expect(api.POST.mock.calls[0]![1].body.startTime).toBeNull()
    expect(dialog().text()).toContain('startTime is required.')
    expect(dialog().find('input[aria-label="Starts (your local time)"]').attributes('aria-invalid')).toBe('true')
  })

  it('shows what changed elsewhere when the save conflicts', async () => {
    api.PUT.mockResolvedValue(refused({ ...friday(), serverConfig: { serverName: 'Renamed server' }, version: 5 }, 409))
    await mountPage()

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()
    await click('Save', dialog())

    expect(dialog().text()).toContain('Changed elsewhere')
    expect(dialog().find('.n-table').text()).toContain('Server overrides')
  })

  it('asks before activating, then says what the server answered', async () => {
    api.POST.mockResolvedValue(answer({ message: 'Activation started.', cupId: 1 }, 202))
    await mountPage()

    await labelled('Activate Friday Derby').trigger('click')
    await flushPromises()
    expect(api.POST).not.toHaveBeenCalled()
    expect(confirmDialog().text()).toContain('the server restarts')

    await click('Activate', confirmDialog())

    expect(api.POST).toHaveBeenCalledWith('/api/cups/{id}/activate', { params: { path: { id: 1 } } })
    expect(body().text()).toContain('Activation started.')
  })

  it('says why an activation was refused', async () => {
    api.POST.mockResolvedValue(refused({ title: 'A server restart is already in progress.', status: 409 }, 409))
    await mountPage()

    await labelled('Activate Friday Derby').trigger('click')
    await flushPromises()
    await click('Activate', confirmDialog())

    expect(body().text()).toContain('A server restart is already in progress.')
  })

  it('deletes with the version it showed', async () => {
    api.DELETE.mockResolvedValue(answer(undefined, 204))
    await mountPage()

    await labelled('Delete Friday Derby').trigger('click')
    await flushPromises()
    await click('Delete', confirmDialog())

    expect(api.DELETE).toHaveBeenCalledWith('/api/cups/{id}', { params: { path: { id: 1 } }, headers: { 'If-Match': '"3"' } })
    expect(wrapper!.text()).not.toContain('Friday Derby')
  })

  // Deleting a cup someone just changed would lose their change: show it instead.
  it('does not delete a cup changed meanwhile, and shows the change', async () => {
    api.DELETE.mockResolvedValue(refused({ ...friday(), name: 'Friday Derby (moved)', version: 4 }, 409))
    await mountPage()

    await labelled('Delete Friday Derby').trigger('click')
    await flushPromises()
    await click('Delete', confirmDialog())

    expect(wrapper!.text()).toContain('Friday Derby (moved)')
    expect(body().text()).toContain('was changed meanwhile')
  })

  // The form shows the start to the minute, in local time: a save that did not touch it
  // must send it back exactly, seconds and daylight-saving hour included.
  it('keeps the start instant when the start was not edited', async () => {
    const odd = { ...friday(), startTime: '2026-10-25T01:30:45Z' }
    api.GET.mockImplementation((path: string) =>
      path === '/api/cups' ? Promise.resolve(answer({ count: 1, cups: [odd] })) : path === '/api/collections' ? Promise.resolve(answer([])) : Promise.resolve(answer([])),
    )
    api.PUT.mockResolvedValue(answer({ ...odd, version: 4 }))
    await mountPage()

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()
    await dialog().find('input[aria-label="Name"]').setValue('Renamed')
    await click('Save', dialog())

    expect(api.PUT.mock.calls[0]![1].body.startTime).toBe('2026-10-25T01:30:45Z')
  })

  // "" removes the server's password; null keeps it. A rename must not turn one into the other.
  it('keeps a "no password" override', async () => {
    const open = { ...sunday(), serverConfig: { password: '' } }
    api.GET.mockImplementation((path: string) =>
      path === '/api/cups' ? Promise.resolve(answer({ count: 1, cups: [open] })) : Promise.resolve(answer([])),
    )
    api.PUT.mockResolvedValue(answer({ ...open, version: 4 }))
    await mountPage()

    await labelled('Edit Sunday Race').trigger('click')
    await flushPromises()
    await dialog().find('input[aria-label="Name"]').setValue('Open Sunday')
    await click('Save', dialog())

    expect(api.PUT.mock.calls[0]![1].body.serverConfig).toEqual(expect.objectContaining({ password: '' }))
  })

  it('does not send "a collection" without one', async () => {
    await mountPage()

    await labelled('Edit Sunday Race').trigger('click')
    await flushPromises()
    editor().draft.source = 'collection'
    await flushPromises()
    await click('Save', dialog())

    expect(api.PUT).not.toHaveBeenCalled()
    expect(dialog().text()).toContain('Pick a collection')
  })

  it('shows a changed track setting in the conflict', async () => {
    api.PUT.mockResolvedValue(refused({ ...sunday(), tracks: [{ track: 'arena', gamemode: 'derby', weather: 'rain', laps: 8 }], version: 5 }, 409))
    await mountPage()

    await labelled('Edit Sunday Race').trigger('click')
    await flushPromises()
    await click('Save', dialog())

    expect(dialog().find('.n-table').text()).toContain('laps 8')
  })

  it('refreshes when the hub says a cup became active', async () => {
    await mountPage()
    api.GET.mockImplementation((path: string) =>
      Promise.resolve(answer(path === '/api/cups' ? { count: 2, cups: [{ ...friday(), isActive: true }, { ...sunday(), isActive: false }] } : [])),
    )

    hub.get('CupActivated')!({ cupId: 1, cupName: 'Friday Derby', timestamp: when })
    await flushPromises()

    expect(labelled('Activate Friday Derby').attributes('disabled')).toBeDefined()
    expect(labelled('Activate Sunday Race').attributes('disabled')).toBeUndefined()
  })

  it('names its choice groups as radio groups', async () => {
    await mountPage()

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()

    for (const [label, name] of [['Repeats', 'cup-repeat'], ['Rotation', 'cup-rotation']]) {
      const group = dialog().find(`[aria-label="${label}"]`)
      expect(group.attributes('role')).toBe('radiogroup')
      expect(group.findAll('input[type="radio"]').every((r) => r.attributes('name') === name)).toBe(true)
    }
  })

  // "Keep mine" rebases onto their version; my start, which I did not edit, stays mine.
  it('keeps the start instant through a conflict retry', async () => {
    const odd = { ...friday(), startTime: '2026-10-25T01:30:45Z' }
    api.GET.mockImplementation((path: string) =>
      Promise.resolve(answer(path === '/api/cups' ? { count: 1, cups: [odd] } : [])),
    )
    api.PUT.mockResolvedValueOnce(refused({ ...odd, startTime: '2026-10-25T03:10:50Z', version: 5 }, 409))
    api.PUT.mockResolvedValueOnce(answer({ ...odd, version: 6 }))
    await mountPage()

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()
    await dialog().find('input[aria-label="Name"]').setValue('Renamed')
    await click('Save', dialog())
    await click('Keep mine', dialog())
    await click('Save', dialog())

    expect(api.PUT.mock.calls[1]![1].headers).toEqual({ 'If-Match': '"5"' })
    expect(api.PUT.mock.calls[1]![1].body.startTime).toBe('2026-10-25T01:30:45Z')
  })

  it('sends a start that was edited', async () => {
    api.PUT.mockResolvedValue(answer({ ...friday(), version: 4 }))
    await mountPage()

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()
    await dialog().find('input[aria-label="Starts (your local time)"]').setValue('2026-11-06T21:15')
    await click('Save', dialog())

    expect(api.PUT.mock.calls[0]![1].body.startTime).toBe(new Date('2026-11-06T21:15').toISOString())
  })

  it('sends each password choice as the API means it', async () => {
    api.PUT.mockResolvedValue(answer({ ...friday(), version: 4 }))
    await mountPage()
    const choose = async (label: string) => {
      if (!dialog().find('[aria-label="Password"]').exists()) {
        await dialog().find('.n-collapse-item__header-main').trigger('click')
        await flushPromises()
      }
      await dialog().find('[aria-label="Password"]').findAll('.n-radio-button').find((b) => b.text() === label)!.trigger('click')
      await flushPromises()
    }
    const sent = () => api.PUT.mock.calls.at(-1)![1].body.serverConfig

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()
    await choose('Set one')
    expect(dialog().find('[aria-label="Password"]').attributes('role')).toBe('radiogroup')
    // Set, but empty: stopped here.
    await click('Save', dialog())
    expect(api.PUT).not.toHaveBeenCalled()
    await dialog().find('input[aria-label="Cup password"]').setValue(' s3cret ')
    await click('Save', dialog())
    expect(sent().password).toBe(' s3cret ')

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()
    await choose('No password')
    await click('Save', dialog())
    expect(sent().password).toBe('')

    await labelled('Edit Friday Derby').trigger('click')
    await flushPromises()
    await choose("Keep the server's")
    await click('Save', dialog())
    expect(sent().password).toBeNull()
  })

  it('shows changed weather and a changed rotation name in the conflict', async () => {
    api.PUT.mockResolvedValue(refused({ ...sunday(), collectionName: 'Renamed set', tracks: [{ track: 'arena', gamemode: 'derby', weather: 'fog' }], version: 5 }, 409))
    await mountPage()

    await labelled('Edit Sunday Race').trigger('click')
    await flushPromises()
    await click('Save', dialog())

    const table = dialog().find('.n-table').text()
    expect(table).toContain('weather fog')
    expect(table).toContain('Renamed set')
  })

  it('refreshes when the hub says a cup run ended', async () => {
    await mountPage()
    api.GET.mockImplementation((path: string) =>
      Promise.resolve(answer(path === '/api/cups' ? { count: 2, cups: [{ ...friday(), lastOccurrence: when, lastOutcome: 'Failed' }, sunday()] } : [])),
    )

    hub.get('CupOccurrenceEnded')!({ cupId: 1, cupName: 'Friday Derby', occurrence: when, outcome: 'Failed', timestamp: when })
    await flushPromises()

    expect(wrapper!.text()).toContain('(Failed)')
  })

  // No hub: polling sees the activation through a full warning and lobby wait.
  it('keeps checking until an activation completes, without the hub', async () => {
    vi.useFakeTimers()
    try {
      api.POST.mockResolvedValue(answer({ message: 'Activation started.', cupId: 1 }, 202))
      await mountPage()
      await labelled('Activate Friday Derby').trigger('click')
      await flushPromises()
      await click('Activate', confirmDialog())

      await vi.advanceTimersByTimeAsync(14 * 60_000)
      api.GET.mockImplementation((path: string) =>
        Promise.resolve(answer(path === '/api/cups' ? { count: 2, cups: [{ ...friday(), isActive: true }, { ...sunday(), isActive: false }] } : [])),
      )
      await vi.advanceTimersByTimeAsync(10_000)
      await flushPromises()

      expect(labelled('Activate Friday Derby').attributes('disabled')).toBeDefined()
      const calls = api.GET.mock.calls.length
      await vi.advanceTimersByTimeAsync(60_000)
      // Stopped once it saw the cup active.
      expect(api.GET.mock.calls.length).toBe(calls)
    } finally {
      vi.useRealTimers()
    }
  })
})
