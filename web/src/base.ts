/**
 * The path the site lives under, always ending in `/`: `/tvtp2/` for the server the portal
 * lists as `tvtp2`, or `/` from the Vite dev server. The portal writes it into index.html's
 * `<base>` (Portal/WreckfestPortal/ControllerSite.cs), so one build works for every server.
 */
export function basePath(baseURI: string = document.baseURI): string {
  const path = new URL(baseURI).pathname
  return path.endsWith('/') ? path : path.slice(0, path.lastIndexOf('/') + 1)
}

export const BASE = basePath()

/**
 * The portal's id for this server: its path without slashes (`tvtp2` for `/tvtp2/`), which
 * the portal's accounts grant servers by. Empty at the root, as from the Vite dev server.
 */
export function serverId(base: string = BASE): string {
  return base.split('/').find((segment) => segment.length > 0) ?? ''
}

export const SERVER_ID = serverId()

/** A site path such as `/api/auth/state`, under the base: `/tvtp2/api/auth/state`. */
export function sitePath(path: string, base: string = BASE): string {
  return base + path.replace(/^\/+/, '')
}

/** The reverse of {@link sitePath}: a full path with the base taken off, starting with `/`. */
export function withoutBase(path: string, base: string = BASE): string {
  return path.startsWith(base) ? '/' + path.slice(base.length) : path
}
