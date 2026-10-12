import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { DOMWrapper, flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import TrackDumpImport from './TrackDumpImport.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const counts = (added: number, updated = 0, unchanged = 0, skipped = 0) => ({ added, updated, unchanged, skipped })
const report = (extra: Record<string, unknown> = {}) => ({
  tool: 'WreckfestTrackDump 1.0.0',
  generatedAt: '2026-10-06T14:03:12Z',
  mods: counts(1),
  tracks: counts(2, 0, 1),
  variants: counts(7, 3, 40, 2),
  skipped: [{ id: 'workshop:1/gardemoen/gardemoen_head-on', reason: 'Its id has characters the catalogue does not allow.' }],
  diagnostics: [{ code: 'missing-value', severity: 'warning', count: 12, meaning: 'A value that should be set was empty.' }],
  ...extra,
})

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper<InstanceType<typeof TrackDumpImport>> | undefined
const body = () => new DOMWrapper(document.body)
const button = (text: string) => body().findAll('button').find((b) => b.text().trim() === text)!

async function openWith(file: { name: string; size: number; text: () => Promise<string> } | File) {
  wrapper = mount(TrackDumpImport, { attachTo: document.body })
  wrapper.vm.start()
  await flushPromises()
  const input = body().find('input[type="file"]')
  Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
  await input.trigger('change')
  await flushPromises()
}

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('TrackDumpImport', () => {
  it('uploads the file as it is, and shows what the import did', async () => {
    api.POST.mockResolvedValue(answer(report()))
    const json = '{"formatVersion":1,"sources":[],"tracks":[]}'
    await openWith(new File([json], 'tracks.json', { type: 'application/json' }))

    await button('Import').trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledTimes(1)
    const [path, options] = api.POST.mock.calls[0]!
    expect(path).toBe('/api/catalogue/import/trackdump')
    expect(options.body).toBe(json)
    expect(options.bodySerializer(options.body)).toBe(json)
    expect(options.headers).toEqual({ 'Content-Type': 'application/json' })

    const text = body().text()
    expect(text).toContain('Added 1 mod, 2 tracks and 7 variants. Filled in 3 track lengths. Skipped 2 items, listed below.')
    expect(text).toContain('New variants are not votable until you allow them.')
    expect(text).toContain('A value that should be set was empty.')
    expect(text).toContain('WreckfestTrackDump 1.0.0')
    const variants = body().findAll('tbody tr').find((r) => r.find('th').text() === 'Variants')!
    expect(variants.findAll('td').map((c) => c.text())).toEqual(['7', '3', '40', '2'])
    expect(text).toContain('gardemoen_head-on')
    expect(text).toContain('missing-value')
    expect(wrapper!.emitted('imported')).toHaveLength(1)
  })

  it('says when there was nothing new', async () => {
    api.POST.mockResolvedValue(
      answer(report({ mods: counts(0, 0, 1), tracks: counts(0, 0, 3), variants: counts(0, 0, 40), skipped: [], diagnostics: [] })),
    )
    await openWith(new File(['{}'], 'tracks.json'))

    await button('Import').trigger('click')
    await flushPromises()

    expect(body().text()).toContain('Nothing new')
    // No skipped list and no diagnostics: only the counts.
    expect(body().findAll('h3')).toHaveLength(0)
  })

  it('says nothing was imported when every new item was skipped', async () => {
    api.POST.mockResolvedValue(
      answer(report({ mods: counts(0, 0, 1), tracks: counts(0, 0, 0, 1), variants: counts(0, 0, 0, 3), diagnostics: [] })),
    )
    await openWith(new File(['{}'], 'tracks.json'))

    await button('Import').trigger('click')
    await flushPromises()

    const text = body().text()
    expect(text).toContain('Nothing was imported: 4 items were skipped, listed below.')
    expect(text).not.toContain('Nothing new')
    expect(text).toContain('gardemoen_head-on')
  })

  it('says what was filled in when nothing was added', async () => {
    api.POST.mockResolvedValue(
      answer(report({ mods: counts(0, 1), tracks: counts(0, 0, 3), variants: counts(0, 5, 35), skipped: [], diagnostics: [] })),
    )
    await openWith(new File(['{}'], 'tracks.json'))

    await button('Import').trigger('click')
    await flushPromises()

    expect(body().find('[role="status"]').text()).toBe('Filled in 5 track lengths. Filled in 1 missing mod workshop id.')
  })

  it("shows the server's refusal, such as an unknown format version", async () => {
    const title = 'This tracks.json is formatVersion 2, and this controller reads only formatVersion 1.'
    api.POST.mockResolvedValue(refused({ status: 400, title }, 400))
    await openWith(new File(['{}'], 'tracks.json'))

    await button('Import').trigger('click')
    await flushPromises()

    expect(body().text()).toContain(title)
    expect(wrapper!.emitted('imported')).toBeUndefined()
  })

  it('does not upload a file over the limit', async () => {
    const text = vi.fn()
    await openWith({ name: 'huge.json', size: 16 * 1024 * 1024 + 1, text })

    expect(body().text()).toContain('over the 16 MB')
    expect(button('Import').attributes('disabled')).toBeDefined()
    await button('Import').trigger('click')
    expect(text).not.toHaveBeenCalled()
    expect(api.POST).not.toHaveBeenCalled()
  })

  it('says when no answer came, so it is not known whether the import happened', async () => {
    api.POST.mockRejectedValue(new TypeError('Failed to fetch'))
    await openWith(new File(['{}'], 'tracks.json'))

    await button('Import').trigger('click')
    await flushPromises()

    expect(body().text()).toContain('No answer from the controller')
  })

  describe('with a dump folder', () => {
    const dumpJson = JSON.stringify({
      formatVersion: 1,
      sources: [],
      tracks: [
        {
          source: 'base',
          images: { small: 'data/menu/textures/env_a_small.bmap', large: 'data/menu/textures/env_a_large.bmap', loading: 'data/menu/textures/env_a_loading.bmap' },
          variants: [{ images: { small: 'data/menu/textures/event_a1_small.bmap', large: null } }],
        },
      ],
    })
    const inFolder = (path: string, content = 'png') => {
      const file = new File([content], path.split('/').pop()!)
      Object.defineProperty(file, 'webkitRelativePath', { value: path })
      return file
    }
    const imageReport = (extra: Record<string, unknown> = {}) => ({ stored: 2, unchanged: 1, removed: 0, notUsed: 0, refused: 0, unmatched: 0, ...extra })

    async function openFolder(files: File[]) {
      wrapper = mount(TrackDumpImport, { attachTo: document.body })
      wrapper.vm.start()
      await flushPromises()
      const input = body().find('input[webkitdirectory]')
      Object.defineProperty(input.element, 'files', { value: files, configurable: true })
      await input.trigger('change')
      await flushPromises()
    }

    it('imports the tracks.json, then only the small and large images it names', async () => {
      api.POST.mockResolvedValueOnce(answer(report())).mockResolvedValueOnce(answer(imageReport()))
      await openFolder([
        inFolder('dump/base/env_a_small.png'),
        inFolder('dump/base/env_a_large.png'),
        inFolder('dump/base/env_a_loading.png'),
        inFolder('dump/base/event_a1_small.png'),
        inFolder('dump/base/unrelated.png'),
        inFolder('dump/tracks.json', dumpJson),
      ])
      expect(body().text()).toContain('tracks.json and 3 images found')

      await button('Import').trigger('click')
      await flushPromises()

      expect(api.POST).toHaveBeenCalledTimes(2)
      expect(api.POST.mock.calls[0]![0]).toBe('/api/catalogue/import/trackdump')
      expect(api.POST.mock.calls[0]![1].body).toBe(dumpJson)
      const [path, options] = api.POST.mock.calls[1]!
      expect(path).toBe('/api/catalogue/import/trackdump/images')
      const form = options.bodySerializer() as FormData
      expect((form.get('dump') as File).name).toBe('tracks.json')
      expect((form.getAll('images') as File[]).map((f) => f.name).sort()).toEqual([
        'dump/base/env_a_large.png',
        'dump/base/env_a_small.png',
        'dump/base/event_a1_small.png',
      ])
      expect(body().text()).toContain('Images: 2 stored, 1 already here.')
      expect(wrapper!.emitted('imported')).toHaveLength(1)
    })

    it('says where the image import stopped', async () => {
      api.POST.mockResolvedValueOnce(answer(report())).mockResolvedValueOnce(refused({ title: 'The upload is over the 64 MB one image batch takes.' }, 413))
      await openFolder([inFolder('dump/base/env_a_small.png'), inFolder('dump/tracks.json', dumpJson)])

      await button('Import').trigger('click')
      await flushPromises()

      expect(body().text()).toContain('over the 64 MB')
      // The tracks were imported either way.
      expect(wrapper!.emitted('imported')).toHaveLength(1)
    })

    it('sends the dump alone when the folder has no images, so dropped ones are removed', async () => {
      api.POST.mockResolvedValueOnce(answer(report())).mockResolvedValueOnce(answer(imageReport({ stored: 0, unchanged: 0, removed: 3 })))
      await openFolder([inFolder('dump/tracks.json', dumpJson)])

      await button('Import').trigger('click')
      await flushPromises()

      expect(api.POST).toHaveBeenCalledTimes(2)
      const form = api.POST.mock.calls[1]![1].bodySerializer() as FormData
      expect(form.getAll('images')).toHaveLength(0)
      expect(body().text()).toContain('Removed 3 images')
    })

    it('needs a tracks.json in the folder', async () => {
      await openFolder([inFolder('dump/base/env_a_small.png')])

      expect(body().text()).toContain('There is no tracks.json in that folder')
      expect(button('Import').attributes('disabled')).toBeDefined()
    })
  })
})
