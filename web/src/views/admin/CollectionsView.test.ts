import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import CollectionsView from './CollectionsView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const when = '2026-09-29T10:00:00Z'
const summary = (id: number, name: string, trackCount: number, version = 1) => ({ id, name, version, trackCount, createdAt: when, updatedAt: when })
const track = (id: string, extra: Record<string, unknown> = {}) => ({
  track: id,
  variant: null,
  gamemode: null,
  laps: null,
  bots: null,
  numTeams: null,
  carResetDisabled: null,
  wrongWayLimiterDisabled: null,
  carClassRestriction: null,
  carRestriction: null,
  weather: null,
  ...extra,
})
const collection = (id: number, name: string, tracks: ReturnType<typeof track>[], version = 1) => ({
  id,
  name,
  version,
  createdAt: when,
  updatedAt: when,
  tracks,
})
const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

function mountPage() {
  wrapper = mount(
    defineComponent({ render: () => h(NMessageProvider, () => h(NDialogProvider, () => h(CollectionsView))) }),
    { attachTo: document.body },
  )
  return wrapper
}

const body = () => new DOMWrapper(document.body)
const button = (label: string, within = body()) => within.findAll('button').find((b) => b.text().trim() === label)!
const rowOf = (name: string) => wrapper!.findAll('tr').find((row) => row.text().includes(name))!

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
  api.GET.mockImplementation((path: string) => {
    if (path === '/api/collections') return Promise.resolve(answer([summary(1, 'Evening', 2, 4), summary(2, 'Empty', 0)]))
    if (path === '/api/catalogue/variants') return Promise.resolve(answer([]))
    if (path === '/api/collections/{id}') {
      return Promise.resolve(answer(collection(1, 'Evening', [track('loop', { laps: 3, weather: 'rain' }), track('arena')], 4)))
    }
    throw new Error(`unexpected GET ${path}`)
  })
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('CollectionsView', () => {
  it('lists the collections with their track counts', async () => {
    mountPage()
    await flushPromises()

    expect(rowOf('Evening').text()).toContain('2')
    // Nothing to deploy in an empty collection.
    expect(button('Deploy', rowOf('Empty')).attributes('disabled')).toBeDefined()
  })

  // The editor opens on the collection as it is now, and saves every field of every track.
  it('edits a collection: reorders its tracks and saves with its version', async () => {
    api.PUT.mockResolvedValue(answer(collection(1, 'Evening', [track('arena'), track('loop', { laps: 3, weather: 'rain' })], 5)))
    mountPage()
    await flushPromises()

    await button('Edit', rowOf('Evening')).trigger('click')
    await flushPromises()
    await body().find('button[aria-label="Move loop (not in the catalogue) down"]').trigger('click')
    await button('Save').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/collections/{id}', {
      params: { path: { id: 1 } },
      headers: { 'If-Match': '"4"' },
      body: {
        name: 'Evening',
        tracks: [
          expect.objectContaining({ track: 'arena' }),
          expect.objectContaining({ track: 'loop', laps: 3, weather: 'rain' }),
        ],
      },
    })
    expect(api.PUT.mock.calls[0]![1].body.tracks[0]).not.toHaveProperty('variant')
    expect(body().text()).toContain('Collection saved.')
  })

  it('shows what changed elsewhere when the save conflicts', async () => {
    api.PUT.mockResolvedValue(refused(collection(1, 'Evening (edited)', [track('loop')], 5), 409))
    mountPage()
    await flushPromises()

    await button('Edit', rowOf('Evening')).trigger('click')
    await flushPromises()
    await button('Save').trigger('click')
    await flushPromises()

    expect(body().text()).toContain('Changed elsewhere')
    expect(body().text()).toContain('Evening (edited)')
  })

  it('duplicates a collection', async () => {
    api.POST.mockResolvedValue(answer(collection(3, 'Evening (copy)', [track('loop'), track('arena')]), 201))
    mountPage()
    await flushPromises()

    await button('Duplicate', rowOf('Evening')).trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/collections/{id}/duplicate', { params: { path: { id: 1 } } })
    expect(wrapper!.text()).toContain('Evening (copy)')
  })

  it('asks before deploying, then says what the server answered', async () => {
    api.POST.mockResolvedValue(answer({ message: 'Deployed Evening to the server config.', collectionName: 'Evening', count: 2 }))
    mountPage()
    await flushPromises()

    await button('Deploy', rowOf('Evening')).trigger('click')
    await flushPromises()
    expect(api.POST).not.toHaveBeenCalled()
    expect(body().find('.n-dialog').text()).toContain('Replace the server\'s rotation with "Evening" (2 tracks)?')

    await button('Deploy', body().find('.n-dialog')).trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/collections/{id}/deploy', { params: { path: { id: 1 } } })
    expect(body().text()).toContain('Deployed Evening to the server config.')
  })

  // A refused deploy (the config could not be written) is a 409 problem, not a conflict.
  it('shows why a deploy was refused', async () => {
    api.POST.mockResolvedValue(refused({ title: 'server_config.cfg is read-only.', status: 409 }, 409))
    mountPage()
    await flushPromises()

    await button('Deploy', rowOf('Evening')).trigger('click')
    await flushPromises()
    await button('Deploy', body().find('.n-dialog')).trigger('click')
    await flushPromises()

    expect(body().text()).toContain('server_config.cfg is read-only.')
  })

  it('asks before deleting, then deletes', async () => {
    api.DELETE.mockResolvedValue(answer(undefined, 204))
    mountPage()
    await flushPromises()

    await button('Delete', rowOf('Empty')).trigger('click')
    await flushPromises()
    expect(body().find('.n-dialog').text()).toContain('Cups that use it keep its tracks')
    await button('Delete', body().find('.n-dialog')).trigger('click')
    await flushPromises()

    expect(api.DELETE).toHaveBeenCalledWith('/api/collections/{id}', { params: { path: { id: 2 } } })
    expect(wrapper!.text()).not.toContain('Empty')
  })
})
