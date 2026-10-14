/**
 * The path the site lives under, always ending in `/`: `/` at the root, or `/tvtp2/` for a
 * controller with Web:PathBase set, so several controllers can share one domain. The
 * controller writes it into index.html's `<base>` (WebApp.cs), so one build works under any.
 */
export function basePath(baseURI: string = document.baseURI): string {
  const path = new URL(baseURI).pathname
  return path.endsWith('/') ? path : path.slice(0, path.lastIndexOf('/') + 1)
}

export const BASE = basePath()

/** A site path such as `/api/auth/state`, under the base: `/tvtp2/api/auth/state`. */
export function sitePath(path: string, base: string = BASE): string {
  return base + path.replace(/^\/+/, '')
}

/** The reverse of {@link sitePath}: a full path with the base taken off, starting with `/`. */
export function withoutBase(path: string, base: string = BASE): string {
  return path.startsWith(base) ? '/' + path.slice(base.length) : path
}
