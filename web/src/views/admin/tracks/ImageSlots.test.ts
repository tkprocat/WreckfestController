import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NMessageProvider } from 'naive-ui'
import { defineComponent, h } from 'vue'
import ImageSlots from './ImageSlots.vue'
import type { CatalogueImage } from '@/api/images'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn(), DELETE: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })

const image = (extra: Partial<CatalogueImage>): CatalogueImage => ({
  trackId: 4,
  variantId: null,
  size: 'Small',
  origin: 'Import',
  hash: 'aaaa',
  bytes: 10,
  isOverlay: false,
  ...extra,
})

let wrapper: VueWrapper | undefined

function mountSlots(props: { kind: 'track' | 'variant'; id: number; name: string; images: CatalogueImage[] }) {
  const changed = vi.fn()
  wrapper = mount(
    defineComponent({
      setup: () => () => h(NMessageProvider, null, { default: () => h(ImageSlots, { ...props, onChanged: changed }) }),
    }),
    { attachTo: document.body },
  )
  return changed
}

const buttons = (text: string) => wrapper!.findAll('button').filter((b) => b.text().trim() === text)

beforeEach(() => {
  for (const fn of Object.values(api)) fn.mockReset()
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('ImageSlots', () => {
  it('links each size by key, and says where the image comes from', () => {
    mountSlots({ kind: 'track', id: 4, name: 'madman_stadium', images: [image({}), image({ size: 'Large', origin: 'Upload', hash: 'bbbb' })] })

    const sources = wrapper!.findAll('img').map((i) => i.attributes('src'))
    expect(sources[0]).toMatch(/^\/api\/images\/tracks\/madman_stadium\/small\?v=/)
    expect(sources[1]).toMatch(/^\/api\/images\/tracks\/madman_stadium\/large\?v=/)
    expect(wrapper!.text()).toContain('Imported picture.')
    expect(wrapper!.text()).toContain('Your uploaded picture.')
    // Only an upload can be removed here.
    expect(buttons('Remove upload')).toHaveLength(1)
  })

  it("draws a variant's outline over the picture, and says it is one", () => {
    mountSlots({ kind: 'variant', id: 9, name: 'v1', images: [image({}), image({ trackId: null, variantId: 9, isOverlay: true, hash: 'c' })] })

    const sources = wrapper!.findAll('img').map((i) => i.attributes('src'))
    expect(sources.filter((s) => s?.startsWith('/api/images/variants/v1/layout'))).toHaveLength(2)
    expect(wrapper!.text()).toContain('Imported layout outline')
  })

  it("says a variant without images shows its track's", () => {
    mountSlots({ kind: 'variant', id: 9, name: 'bigstadium_figure_8', images: [image({})] })

    expect(wrapper!.findAll('img')[0]!.attributes('src')).toMatch(/^\/api\/images\/variants\/bigstadium_figure_8\/small/)
    expect(wrapper!.text()).toContain("shows its track's")
  })

  it('uploads the chosen file for that size, and passes on the answer', async () => {
    const own = [image({ trackId: null, variantId: 9, origin: 'Upload' })]
    api.PUT.mockResolvedValue(answer(own))
    const changed = mountSlots({ kind: 'variant', id: 9, name: 'v', images: [] })

    const input = wrapper!.findAll('input[type="file"]')[0]!
    const file = new File(['png'], 'mine.png', { type: 'image/png' })
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true })
    await input.trigger('change')
    await flushPromises()

    const [path, options] = api.PUT.mock.calls[0]!
    expect(path).toBe('/api/catalogue/variants/{id}/images/{size}')
    expect(options.params.path).toEqual({ id: 9, size: 'Small' })
    expect((options.bodySerializer() as FormData).get('file')).toBeInstanceOf(File)
    expect(changed).toHaveBeenCalledWith(own)
  })

  it('removes an upload, leaving the imported image', async () => {
    api.DELETE.mockResolvedValue(answer(undefined, 204))
    const imported = image({ size: 'Large' })
    const changed = mountSlots({ kind: 'track', id: 4, name: 't', images: [imported, image({ size: 'Large', origin: 'Upload', hash: 'b' })] })

    await buttons('Remove upload')[0]!.trigger('click')
    await flushPromises()

    expect(api.DELETE).toHaveBeenCalledWith('/api/catalogue/tracks/{id}/images/{size}', { params: { path: { id: 4, size: 'Large' } } })
    expect(changed).toHaveBeenCalledWith([imported])
  })
})
