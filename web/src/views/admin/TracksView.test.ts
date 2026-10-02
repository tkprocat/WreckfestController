import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import TracksView from './TracksView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const tag = (slug: string, name: string) => ({ id: slug.length, name, slug, color: null })
const variant = (id: number, trackId: number, variantId: string, name: string, extra: Record<string, unknown> = {}) => ({
  id,
  variantId,
  name,
  gameMode: 'Racing',
  trackId,
  trackKey: `t${trackId}`,
  trackName: trackId === 1 ? 'Fields' : 'Crash Arena',
  allowedForVoting: true,
  isBuiltIn: true,
  isHidden: false,
  version: 1,
  tags: [] as ReturnType<typeof tag>[],
  ...extra,
})
const track = (id: number, name: string, variants: ReturnType<typeof variant>[], extra: Record<string, unknown> = {}) => ({
  id,
  key: `t${id}`,
  name,
  origin: 'BaseGame',
  dlcName: null,
  mod: null,
  isBuiltIn: true,
  isHidden: false,
  version: 1,
  weather: ['clear'],
  variants,
  ...extra,
})

const fields = () =>
  track(1, 'Fields', [variant(11, 1, 'loop', 'Loop', { tags: [tag('short', 'Short')] }), variant(12, 1, 'loop_rev', 'Loop Reverse', { isHidden: true })])
const arena = () =>
  track(2, 'Crash Arena', [variant(21, 2, 'bowl', 'Bowl', { gameMode: 'Derby', isBuiltIn: false })], { isBuiltIn: false, origin: 'Custom', weather: ['clear', 'rain'] })
const hiddenTrack = () => track(3, 'Old Quarry', [variant(31, 3, 'quarry', 'Quarry')], { isHidden: true })

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

async function mountPage() {
  wrapper = mount(
    defineComponent({ render: () => h(NMessageProvider, () => h(NDialogProvider, () => h(TracksView))) }),
    { attachTo: document.body },
  )
  await flushPromises()
  return wrapper
}

const body = () => new DOMWrapper(document.body)
const labelled = (label: string) => body().find(`[aria-label="${label}"]`)
const menuOption = (action: string) => body().findAll('.n-dropdown-option-body').find((option) => option.text().trim() === action)!
async function menuAction(row: string, action: string) {
  await body().find('[aria-label="More actions for ' + row + '"]').trigger('click')
  await flushPromises()
  await menuOption(action).trigger('click')
  await flushPromises()
}

const trackNames = () => wrapper!.findAll('tbody tr').map((r) => r.text())

async function expand(name: string) {
  await labelled(`Variants of ${name}`).trigger('click')
  await flushPromises()
}

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
  api.GET.mockImplementation((path: string) => {
    if (path === '/api/catalogue/tracks') return Promise.resolve(answer([arena(), fields(), hiddenTrack()]))
    if (path === '/api/catalogue/weather') return Promise.resolve(answer(['clear', 'rain', 'fog']))
    if (path === '/api/catalogue/mods') return Promise.resolve(answer([]))
    if (path === '/api/catalogue/tags') return Promise.resolve(answer([tag('short', 'Short'), tag('night', 'Night')]))
    throw new Error(`unexpected GET ${path}`)
  })
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('TracksView', () => {
  it('asks for hidden tracks too, and shows them only when asked', async () => {
    await mountPage()

    expect(api.GET).toHaveBeenCalledWith('/api/catalogue/tracks', { params: { query: { includeHidden: true } } })
    expect(wrapper!.text()).toContain('Fields')
    expect(wrapper!.text()).not.toContain('Old Quarry')

    await body().find('.n-checkbox').trigger('click')
    expect(wrapper!.text()).toContain('Old Quarry')
  })

  it('finds a track by the name of one of its variants', async () => {
    await mountPage()

    await labelled('Search tracks').setValue('bowl')

    expect(trackNames().join()).toContain('Crash Arena')
    expect(trackNames().join()).not.toContain('Fields')
  })

  it('lists a track\'s variants, leaving hidden ones out', async () => {
    await mountPage()

    await expand('Fields')

    expect(wrapper!.text()).toContain('Loop')
    expect(wrapper!.text()).toContain('Short')
    expect(wrapper!.text()).not.toContain('Loop Reverse')
  })

  it('hides a track and shows the server\'s answer', async () => {
    api.POST.mockResolvedValue(answer({ ...fields(), isHidden: true, version: 2 }))
    await mountPage()

    await menuAction('Fields', 'Hide')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/catalogue/tracks/{id}/hide', { params: { path: { id: 1 } } })
    expect(wrapper!.findAll('tbody tr').some((r) => r.text().includes('Fields'))).toBe(false)
    expect(body().text()).toContain('"Fields" hidden.')
  })

  it('turns voting off for a variant', async () => {
    api.PUT.mockResolvedValue(answer({ ...fields().variants[0], allowedForVoting: false, version: 2 }))
    await mountPage()
    await expand('Fields')

    await labelled('Players can vote for Fields - Loop').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/catalogue/variants/{id}/voting', { params: { path: { id: 11 } }, body: { allowed: false } })
    expect(labelled('Players can vote for Fields - Loop').attributes('aria-checked')).toBe('false')
  })

  it('asks before resetting a built-in track', async () => {
    api.POST.mockResolvedValue(answer(fields()))
    await mountPage()

    await menuAction('Fields', 'Reset')
    await flushPromises()
    expect(api.POST).not.toHaveBeenCalled()
    expect(body().find('.n-dialog').text()).toContain('Its variants are left alone.')

    await body().find('.n-dialog').findAll('button').find((b) => b.text() === 'Reset')!.trigger('click')
    await flushPromises()
    expect(api.POST).toHaveBeenCalledWith('/api/catalogue/tracks/{id}/reset', { params: { path: { id: 1 } } })
  })

  // Built-ins can only be hidden; an added track can be deleted, unless a collection uses it.
  it('says why a delete was refused', async () => {
    api.DELETE.mockResolvedValue(refused({ title: 'Collection "Evening" uses bowl.', status: 409 }, 409))
    await mountPage()
    await labelled('More actions for Fields').trigger('click')
    await flushPromises()
    expect(body().findAll('.n-dropdown-option-body').some((option) => option.text().trim() === 'Delete')).toBe(false)

    await menuAction('Crash Arena', 'Delete')
    await flushPromises()
    await body().find('.n-dialog').findAll('button').find((b) => b.text() === 'Delete')!.trigger('click')
    await flushPromises()

    expect(body().text()).toContain('Collection "Evening" uses bowl.')
    expect(wrapper!.text()).toContain('Crash Arena')
  })

  // The table's own trigger is a click-only div; the button in it is what keyboards reach.
  it("opens a track's variants from a button that says whether it is open", async () => {
    await mountPage()
    const toggle = () => labelled('Variants of Fields')
    expect(toggle().element.tagName).toBe('BUTTON')
    expect(toggle().attributes('aria-expanded')).toBe('false')

    await expand('Fields')

    expect(toggle().attributes('aria-expanded')).toBe('true')
    expect(labelled('Players can vote for Fields - Loop').exists()).toBe(true)
  })

  it('does not find a track by a variant the filters hide', async () => {
    await mountPage()

    await labelled('Search tracks').setValue('loop_rev')
    expect(trackNames().join()).not.toContain('Fields')

    await body().find('.n-checkbox').trigger('click')
    expect(trackNames().join()).toContain('Fields')
  })

  // Someone else saved the track at the same moment: show theirs, not what this page assumed.
  it('shows the saved track when an action lost a race', async () => {
    api.POST.mockResolvedValue(refused({ ...fields(), name: 'Fields (renamed)', version: 3 }, 409))
    await mountPage()

    await menuAction('Fields', 'Hide')
    await flushPromises()

    expect(wrapper!.text()).toContain('Fields (renamed)')
    expect(body().text()).toContain('Someone else changed this at the same moment')
  })

  it('shows the saved variant when a voting change lost a race', async () => {
    api.PUT.mockResolvedValue(refused({ ...fields().variants[0], name: 'Loop (renamed)', allowedForVoting: true, version: 3 }, 409))
    await mountPage()
    await expand('Fields')

    await labelled('Players can vote for Fields - Loop').trigger('click')
    await flushPromises()

    expect(labelled('Players can vote for Fields - Loop (renamed)').attributes('aria-checked')).toBe('true')
    expect(body().text()).toContain('Someone else changed this at the same moment')
  })

  it('names every filter', async () => {
    await mountPage()

    for (const label of ['Origin', 'Game mode', 'Tag', 'Weather']) {
      expect(body().find(`input[aria-label="${label}"]`).exists(), label).toBe(true)
    }
  })

  it('filters by game mode and weather', async () => {
    await mountPage()
    const vm = wrapper!.findComponent(TracksView).vm as unknown as { mode: string | null; weather: string | null }

    vm.mode = 'Derby'
    await flushPromises()
    expect(trackNames().join()).not.toContain('Fields')

    vm.mode = null
    vm.weather = 'rain'
    await flushPromises()
    expect(trackNames().join()).toContain('Crash Arena')
    expect(trackNames().join()).not.toContain('Fields')
  })

  describe('editing', () => {
    const dialog = () => body().find('.n-card[role="dialog"], .n-modal .n-card')
    const input = (label: string) => dialog().find(`input[aria-label="${label}"]`)
    const submit = async (label: string) => {
      await dialog().findAll('button').find((b) => b.text() === label)!.trigger('click')
      await flushPromises()
    }

    // A new track supports every weather, as the API adds it: no second request.
    it('adds a track', async () => {
      api.POST.mockResolvedValue(answer(track(4, 'Test Loop', [], { key: 'test_loop', origin: 'Custom', isBuiltIn: false, weather: ['clear', 'rain', 'fog'] }), 201))
      await mountPage()

      await wrapper!.findAll('button').find((b) => b.text() === 'Add track')!.trigger('click')
      await flushPromises()
      await input('Name').setValue('Test Loop')
      await input('Key').setValue('test_loop')
      await submit('Add')

      expect(api.POST).toHaveBeenCalledWith('/api/catalogue/tracks', {
        body: { key: 'test_loop', name: 'Test Loop', origin: 'Custom', dlcName: null, modId: null },
      })
      expect(api.PUT).not.toHaveBeenCalled()
      expect(wrapper!.text()).toContain('Test Loop')
    })

    it('edits a track with its version, then saves the weather it changed', async () => {
      api.PUT.mockImplementation((path: string) =>
        Promise.resolve(
          path === '/api/catalogue/tracks/{id}'
            ? answer({ ...fields(), name: 'Green Fields', version: 2 })
            : answer({ ...fields(), name: 'Green Fields', weather: ['clear', 'rain'], version: 3 }),
        ),
      )
      await mountPage()

      await labelled('Edit Fields').trigger('click')
      await flushPromises()
      // A built-in's key is fixed.
      expect(input('Key').attributes('disabled')).toBeDefined()
      await input('Name').setValue('Green Fields')
      const vm = wrapper!.findComponent({ name: 'TrackEditor' }).vm as unknown as { draft: { weather: string[] } }
      vm.draft.weather = ['clear', 'rain']
      await submit('Save')

      expect(api.PUT).toHaveBeenNthCalledWith(1, '/api/catalogue/tracks/{id}', {
        params: { path: { id: 1 } },
        body: { key: 't1', name: 'Green Fields', origin: 'BaseGame', dlcName: null, modId: null },
        headers: { 'If-Match': '"1"' },
      })
      expect(api.PUT).toHaveBeenNthCalledWith(2, '/api/catalogue/tracks/{id}/weather', {
        params: { path: { id: 1 } },
        body: { weather: ['clear', 'rain'] },
        headers: { 'If-Match': '"2"' },
      })
      expect(wrapper!.text()).toContain('Green Fields')
      expect(wrapper!.text()).toContain('clear, rain')
    })

    // The track is saved; only the weather is not, and the page says so.
    it('says when the track saved but its weather did not', async () => {
      api.PUT.mockImplementation((path: string) =>
        Promise.resolve(
          path === '/api/catalogue/tracks/{id}'
            ? answer({ ...fields(), name: 'Green Fields', version: 2 })
            : refused({ title: 'Unknown weather: snow.', status: 400, errors: { weather: ['Unknown weather: snow.'] } }, 400),
        ),
      )
      await mountPage()

      await labelled('Edit Fields').trigger('click')
      await flushPromises()
      await input('Name').setValue('Green Fields')
      const vm = wrapper!.findComponent({ name: 'TrackEditor' }).vm as unknown as { draft: { weather: string[] } }
      vm.draft.weather = ['snow']
      await submit('Save')

      expect(body().text()).toContain('The track was saved, but its weather was not')
      expect(wrapper!.text()).toContain('Green Fields')
    })

    const trackEditor = () => wrapper!.findComponent({ name: 'TrackEditor' }).vm as unknown as { draft: { weather: string[] } }

    // Someone else set the weather meanwhile; this form only renamed the track.
    it('does not send weather the form did not change', async () => {
      api.PUT.mockResolvedValue(answer({ ...fields(), name: 'Green Fields', weather: ['fog'], version: 2 }))
      await mountPage()

      await labelled('Edit Fields').trigger('click')
      await flushPromises()
      await input('Name').setValue('Green Fields')
      await submit('Save')

      expect(api.PUT).toHaveBeenCalledTimes(1)
      expect(wrapper!.text()).toContain('fog')
    })

    // Without the weather list the form cannot show a new track's default: it sends none.
    it('keeps a new track\'s default weather when the weather list failed to load', async () => {
      const tracksOnly = api.GET.getMockImplementation()!
      api.GET.mockImplementation((path: string) => (path === '/api/catalogue/weather' ? Promise.reject(new Error('offline')) : tracksOnly(path)))
      api.POST.mockResolvedValue(answer(track(4, 'Test Loop', [], { key: 'test_loop', origin: 'Custom', isBuiltIn: false, weather: ['clear', 'rain', 'fog'] }), 201))
      await mountPage()

      await wrapper!.findAll('button').find((b) => b.text() === 'Add track')!.trigger('click')
      await flushPromises()
      await input('Name').setValue('Test Loop')
      await input('Key').setValue('test_loop')
      await submit('Add')

      expect(api.POST).toHaveBeenCalledTimes(1)
      expect(api.PUT).not.toHaveBeenCalled()
    })

    it('says when the weather lost a race, and shows the saved track', async () => {
      api.PUT.mockImplementation((path: string) =>
        Promise.resolve(
          path === '/api/catalogue/tracks/{id}'
            ? answer({ ...fields(), version: 2 })
            : refused({ ...fields(), name: 'Fields (theirs)', version: 3 }, 409),
        ),
      )
      await mountPage()

      await labelled('Edit Fields').trigger('click')
      await flushPromises()
      trackEditor().draft.weather = ['rain']
      await submit('Save')

      expect(body().text()).toContain('The track was saved, but not its weather')
      expect(wrapper!.text()).toContain('Fields (theirs)')
    })

    it('shows a weather difference in the conflict', async () => {
      api.PUT.mockResolvedValue(refused({ ...fields(), weather: ['fog'], version: 5 }, 409))
      await mountPage()

      await labelled('Edit Fields').trigger('click')
      await flushPromises()
      trackEditor().draft.weather = ['rain']
      await submit('Save')

      expect(body().text()).toContain('Changed elsewhere')
      expect(body().find('.n-table').text()).toContain('Weather')
      expect(body().text()).not.toContain('Your values and the saved ones are the same.')
    })

    // The pickers load before the form opens; the later click wins, whatever answers first.
    it('opens the editor for the latest click when the pickers answer out of order', async () => {
      const pending: ((value: unknown) => void)[] = []
      const tracksOnly = api.GET.getMockImplementation()!
      api.GET.mockImplementation((path: string) =>
        path === '/api/catalogue/weather' ? new Promise((resolve) => pending.push(resolve)) : tracksOnly(path),
      )
      await mountPage()

      await labelled('Edit Fields').trigger('click')
      await labelled('Edit Crash Arena').trigger('click')
      pending[1]!(answer(['clear']))
      await flushPromises()
      pending[0]!(answer(['clear']))
      await flushPromises()

      expect((input('Name').element as HTMLInputElement).value).toBe('Crash Arena')
    })

    const variantEditor = () => wrapper!.findComponent({ name: 'VariantEditor' }).vm as unknown as { draft: { tags: string[] } }

    it('does not send tags the form did not change', async () => {
      api.PUT.mockResolvedValue(answer({ ...fields().variants[0], name: 'Loop 2', tags: [tag('night', 'Night')], version: 2 }))
      await mountPage()
      await expand('Fields')

      await labelled('Edit Loop').trigger('click')
      await flushPromises()
      await input('Name').setValue('Loop 2')
      await submit('Save')

      expect(api.PUT).toHaveBeenCalledTimes(1)
    })

    it('says when the tags lost a race, and shows the saved variant', async () => {
      api.PUT.mockImplementation((path: string) =>
        Promise.resolve(
          path === '/api/catalogue/variants/{id}'
            ? answer({ ...fields().variants[0], version: 2 })
            : refused({ ...fields().variants[0], name: 'Loop (theirs)', version: 3 }, 409),
        ),
      )
      await mountPage()
      await expand('Fields')

      await labelled('Edit Loop').trigger('click')
      await flushPromises()
      variantEditor().draft.tags = ['night']
      await submit('Save')

      expect(body().text()).toContain('The variant was saved, but not its tags')
      expect(wrapper!.text()).toContain('Loop (theirs)')
    })

    it('shows a tag difference in the conflict', async () => {
      api.PUT.mockResolvedValue(refused({ ...fields().variants[0], tags: [], version: 5 }, 409))
      await mountPage()
      await expand('Fields')

      await labelled('Edit Loop').trigger('click')
      await flushPromises()
      variantEditor().draft.tags = ['night']
      await submit('Save')

      expect(body().find('.n-table').text()).toContain('Tags')
    })

    it('opens the variant editor for the latest click when the tags answer out of order', async () => {
      const pending: ((value: unknown) => void)[] = []
      const others = api.GET.getMockImplementation()!
      api.GET.mockImplementation((path: string) =>
        path === '/api/catalogue/tags' ? new Promise((resolve) => pending.push(resolve)) : others(path),
      )
      await mountPage()

      await labelled('Add a variant to Fields').trigger('click')
      await labelled('Add a variant to Crash Arena').trigger('click')
      pending[1]!(answer([]))
      await flushPromises()
      await input('Name').setValue('Half Bowl')
      // The earlier click's answer arrives late: it must not reopen the form over this one.
      pending[0]!(answer([]))
      await flushPromises()

      expect(dialog().text()).toContain('Add a variant to Crash Arena')
      expect((input('Name').element as HTMLInputElement).value).toBe('Half Bowl')
    })

    it('adds a variant under a track, with its tags', async () => {
      const added = variant(13, 1, 'loop_short', 'Loop Short', { isBuiltIn: false })
      api.POST.mockResolvedValue(answer(added, 201))
      api.PUT.mockResolvedValue(answer({ ...added, tags: [tag('night', 'Night')], version: 2 }))
      await mountPage()

      await labelled('Add a variant to Fields').trigger('click')
      await flushPromises()
      await input('Name').setValue('Loop Short')
      await input('Variant id').setValue('loop_short')
      const vm = wrapper!.findComponent({ name: 'VariantEditor' }).vm as unknown as { draft: { tags: string[] } }
      vm.draft.tags = ['night']
      await submit('Add')

      expect(api.POST).toHaveBeenCalledWith('/api/catalogue/variants', {
        body: { variantId: 'loop_short', name: 'Loop Short', gameMode: 'Racing', trackId: 1, allowedForVoting: true },
      })
      expect(api.PUT).toHaveBeenCalledWith('/api/catalogue/variants/{id}/tags', {
        params: { path: { id: 13 } },
        body: { tags: ['night'] },
        headers: { 'If-Match': '"1"' },
      })
      await expand('Fields')
      expect(wrapper!.text()).toContain('Loop Short')
      expect(wrapper!.text()).toContain('Night')
    })

    it('edits a variant with its version and shows a conflict', async () => {
      api.PUT.mockResolvedValue(refused({ ...fields().variants[0], name: 'Loop (theirs)', version: 5 }, 409))
      await mountPage()
      await expand('Fields')

      await labelled('Edit Loop').trigger('click')
      await flushPromises()
      await input('Name').setValue('Loop (mine)')
      await submit('Save')

      expect(api.PUT).toHaveBeenCalledWith('/api/catalogue/variants/{id}', {
        params: { path: { id: 11 } },
        body: { variantId: 'loop', name: 'Loop (mine)', gameMode: 'Racing' },
        headers: { 'If-Match': '"1"' },
      })
      expect(body().text()).toContain('Changed elsewhere')
      expect(body().text()).toContain('Loop (theirs)')
    })
  })
})
