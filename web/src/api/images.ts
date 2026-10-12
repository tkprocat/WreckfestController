import type { components } from '@/api/schema'

/**
 * Catalogue menu images (#260): their URLs, and how a WreckfestTrackDump folder's images
 * are found for an import. The server picks which image a track or variant shows
 * (CatalogueImageStore); these only build links to it.
 */
export type ImageSize = components['schemas']['ImageSize']
export type CatalogueImage = components['schemas']['CatalogueImageResponse']

export const IMAGE_SIZES: ImageSize[] = ['Small', 'Large']

/** A link that changes whenever `version` does, so a changed image is fetched again. */
function withVersion(url: string, version?: string): string {
  return version ? `${url}?v=${encodeURIComponent(version)}` : url
}

export function trackImageUrl(key: string, size: ImageSize, version?: string): string {
  return withVersion(`/api/images/tracks/${encodeURIComponent(key)}/${size.toLowerCase()}`, version)
}

export function variantImageUrl(variantId: string, size: ImageSize, version?: string): string {
  return withVersion(`/api/images/variants/${encodeURIComponent(variantId)}/${size.toLowerCase()}`, version)
}

/**
 * A variant's layout outline, on a clear background: drawn over its picture, as the game
 * does. The base game's variant images are all outlines; a picture never is one.
 */
export function variantLayoutUrl(variantId: string, version?: string): string {
  return withVersion(`/api/images/variants/${encodeURIComponent(variantId)}/layout`, version)
}

/** One string that changes when any of these images does: a cache key for a link to the one picked from them. */
export function imagesVersion(images: CatalogueImage[]): string | undefined {
  return images.length === 0 ? undefined : images.map((i) => i.hash).sort().join('').slice(0, 32)
}

/** Whether any of these is a picture rather than a layout outline: one the picture links can show. */
export function hasPicture(images: CatalogueImage[]): boolean {
  return images.some((i) => !i.isOverlay)
}

/** The images a track has of its own and through its variants. */
export function imagesOfTrack(images: CatalogueImage[], track: { id: number; variants: { id: number }[] }): CatalogueImage[] {
  const variants = new Set(track.variants.map((v) => v.id))
  return images.filter((i) => i.trackId === track.id || (i.variantId != null && variants.has(i.variantId)))
}

const SOURCE_ID = /^(base|workshop:[0-9]{1,20}|mod:[^/\\:]{1,128})$/
const IMAGE_PATH = /^data\/menu\/textures\/([^/\\:]+)\.bmap$/i

/**
 * Where `wftracks imagedump` wrote an image: `<source id, ":" as "_">/<name>.png`. Mirrors
 * TrackDumpImageImporter.FileKey on the server, which decides; this only picks what to send.
 */
export function imageFileKey(sourceId: unknown, imagePath: unknown): string | null {
  if (typeof sourceId !== 'string' || typeof imagePath !== 'string' || !SOURCE_ID.test(sourceId)) return null
  const match = IMAGE_PATH.exec(imagePath)
  return match ? `${sourceId.replaceAll(':', '_')}/${match[1]}.png`.toLowerCase() : null
}

/** A chosen file's key: the last two segments of its path in the folder, as imagedump laid them out. */
export function uploadKey(relativePath: string): string | null {
  const parts = relativePath.split(/[/\\]/).filter((p) => p.length > 0)
  return parts.length >= 2 ? `${parts[parts.length - 2]}/${parts[parts.length - 1]}`.toLowerCase() : null
}

interface DumpImages {
  small?: unknown
  large?: unknown
}
interface DumpRow {
  source?: unknown
  images?: DumpImages | null
  variants?: { images?: DumpImages | null }[] | null
}

/** The keys of every small and large image a tracks.json names. The other sizes are not imported. */
export function dumpImageKeys(dump: unknown): Set<string> {
  const keys = new Set<string>()
  const tracks = (dump as { tracks?: unknown } | null)?.tracks
  if (!Array.isArray(tracks)) return keys
  for (const track of tracks as (DumpRow | null)[]) {
    if (!track) continue
    for (const images of [track.images, ...(Array.isArray(track.variants) ? track.variants.map((v) => v?.images) : [])]) {
      for (const path of [images?.small, images?.large]) {
        const key = imageFileKey(track.source, path)
        if (key) keys.add(key)
      }
    }
  }
  return keys
}

/** Splits files into batches of at most `maxFiles` files and, unless one file alone is larger, `maxBytes`. */
export function batches<T extends { size: number }>(files: T[], maxBytes: number, maxFiles: number): T[][] {
  const result: T[][] = []
  let current: T[] = []
  let bytes = 0
  for (const file of files) {
    if (current.length > 0 && (current.length >= maxFiles || bytes + file.size > maxBytes)) {
      result.push(current)
      current = []
      bytes = 0
    }
    current.push(file)
    bytes += file.size
  }
  if (current.length > 0) result.push(current)
  return result
}
