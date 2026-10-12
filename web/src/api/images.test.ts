import { describe, expect, it } from 'vitest'
import { batches, dumpImageKeys, imageFileKey, imagesVersion, trackImageUrl, uploadKey, variantImageUrl, variantLayoutUrl } from './images'

describe('image links', () => {
  it('name the row by key and the size in lower case, with an optional version', () => {
    expect(trackImageUrl('madman_stadium', 'Small')).toBe('/api/images/tracks/madman_stadium/small')
    expect(variantImageUrl('misc_rbRace', 'Large', 'ab12')).toBe('/api/images/variants/misc_rbRace/large?v=ab12')
    expect(trackImageUrl('a b', 'Large')).toBe('/api/images/tracks/a%20b/large')
    expect(variantLayoutUrl('misc_rbRace', 'c')).toBe('/api/images/variants/misc_rbRace/layout?v=c')
  })

  it('give a version that changes with any image, whatever the order', () => {
    const image = (hash: string) => ({ trackId: 1, variantId: null, size: 'Small' as const, origin: 'Import' as const, hash, bytes: 1, isOverlay: false })
    expect(imagesVersion([])).toBeUndefined()
    expect(imagesVersion([image('b'), image('a')])).toBe(imagesVersion([image('a'), image('b')]))
    expect(imagesVersion([image('a'), image('c')])).not.toBe(imagesVersion([image('a'), image('b')]))
  })
})

describe('dump images', () => {
  it('are found where imagedump writes them, as the server finds them', () => {
    expect(imageFileKey('base', 'data/menu/textures/env_x_small_250x116_raw.bmap')).toBe('base/env_x_small_250x116_raw.png')
    expect(imageFileKey('workshop:123', 'data/menu/textures/E.BMAP')).toBe('workshop_123/e.png')
    expect(imageFileKey('base', 'data/menu/textures/../x.bmap')).toBeNull()
    expect(imageFileKey('C:/x', 'data/menu/textures/x.bmap')).toBeNull()
    expect(imageFileKey('base', null)).toBeNull()
  })

  it('match a chosen file by its last two path segments', () => {
    expect(uploadKey('dump/base/A.png')).toBe('base/a.png')
    expect(uploadKey('a.png')).toBeNull()
  })

  it('are the small and large images of tracks and variants, and no other size', () => {
    const dump = {
      tracks: [
        {
          source: 'base',
          images: { small: 'data/menu/textures/env_a_small.bmap', large: 'data/menu/textures/env_a_large.bmap', loading: 'data/menu/textures/env_a_loading.bmap' },
          variants: [{ images: { small: 'data/menu/textures/event_a1_small.bmap', large: null } }, null],
        },
        null,
        { source: 'workshop:1', images: null, variants: null },
      ],
    }
    expect([...dumpImageKeys(dump)].sort()).toEqual(['base/env_a_large.png', 'base/env_a_small.png', 'base/event_a1_small.png'])
    expect(dumpImageKeys(null).size).toBe(0)
    expect(dumpImageKeys({ tracks: 'no' }).size).toBe(0)
  })

  it('are sent in batches under both limits, a too-large file on its own', () => {
    const files = [5, 5, 5, 20, 1, 1, 1].map((size, i) => ({ size, i }))
    expect(batches(files, 10, 2).map((b) => b.map((f) => f.i))).toEqual([[0, 1], [2], [3], [4, 5], [6]])
    expect(batches([], 10, 2)).toEqual([])
  })
})
