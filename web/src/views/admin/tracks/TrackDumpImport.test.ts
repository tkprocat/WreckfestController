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
  diagnostics: [{ code: 'missing-value', severity: 'warning', count: 12, example: 'The .tcat lists no surfaces.' }],
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
    expect(text).toContain('7 new variants are not votable until you allow them')
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
})
