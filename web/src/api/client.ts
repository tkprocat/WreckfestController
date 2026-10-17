import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'
import type { paths as portalPaths } from './portal-schema'
import { BASE } from '@/base'

// Two typed clients. `api` is this server's controller, under the site's path
// (/tvtp2/api/...), which the portal forwards; `portal` is the portal's own API at the
// root (/api/auth/..., /api/users), where signing in and the accounts live. Types come from
// src/api/schema.d.ts and portal-schema.d.ts, generated from the OpenAPI documents that
// C# tests keep in step with each API.

/**
 * The antiforgery cookie the portal sets for the whole site, and the header it expects the
 * token back in. One token for both clients: the portal checks it on its own API and on
 * every change it forwards to a controller in the user's name.
 */
export const XSRF_COOKIE = 'XSRF-TOKEN'
export const XSRF_HEADER = 'X-XSRF-TOKEN'

const UNSAFE_METHODS = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

export function readCookie(name: string, cookies: string = document.cookie): string | null {
  for (const part of cookies.split(';')) {
    const [key, ...value] = part.trim().split('=')
    if (key === name) {
      return decodeURIComponent(value.join('='))
    }
  }

  return null
}

/** The title of the portal's 400 for a bad token (Portal/WreckfestPortal/Accounts/AntiforgeryFilter.cs). */
export const ANTIFORGERY_FAILURE = 'Missing or invalid antiforgery token.'

/** The token fetch in flight, if any: cleared when it settles, so a failure is not kept. */
let tokenRequest: Promise<void> | null = null

/**
 * Asks the server for a fresh antiforgery token (it arrives as a cookie). The token is
 * tied to who is signed in, so call this after signing in or out. Rejects when the
 * server does not answer with one.
 */
export function refreshAntiforgery(): Promise<void> {
  const request = fetch('/api/auth/antiforgery', { credentials: 'same-origin' }).then((response) => {
    if (!response.ok) {
      throw new Error(`Could not get an antiforgery token (HTTP ${response.status}).`)
    }
  })
  tokenRequest = request
  request.then(
    () => forget(request),
    () => forget(request),
  )
  return request
}

function forget(request: Promise<void>): void {
  if (tokenRequest === request) {
    tokenRequest = null
  }
}

/** For each request sent with a token, a copy to resend once if the token was stale. */
const retries = new WeakMap<Request, Request>()

/**
 * Copies the antiforgery cookie into the header on every request that changes something.
 * A token can go stale - a session that ended leaves the old user's token behind, and a
 * cookie that is present is never refetched - so when the server refuses one, this gets
 * a fresh token and sends the request once more.
 */
export const antiforgery: Middleware = {
  async onRequest({ request }) {
    if (!UNSAFE_METHODS.has(request.method)) {
      return undefined
    }

    if (!readCookie(XSRF_COOKIE)) {
      await (tokenRequest ?? refreshAntiforgery())
    }

    const token = readCookie(XSRF_COOKIE)
    if (token) {
      request.headers.set(XSRF_HEADER, token)
    }

    retries.set(request, request.clone())
    return request
  },

  async onResponse({ request, response }) {
    const retry = retries.get(request)
    retries.delete(request)
    if (!retry || response.status !== 400) {
      return undefined
    }

    const problem = (await response
      .clone()
      .json()
      .catch(() => null)) as { title?: string } | null
    if (problem?.title !== ANTIFORGERY_FAILURE) {
      return undefined
    }

    // The server has answered, refusing the request: no fresh token means that refusal
    // stands. Thrown, it read as no answer at all - "not known whether this happened" (#240).
    try {
      await refreshAntiforgery()
    } catch {
      return undefined
    }

    const token = readCookie(XSRF_COOKIE)
    if (!token) {
      return undefined
    }

    // Straight to fetch, past the middleware: one retry, never a loop.
    retry.headers.set(XSRF_HEADER, token)
    return fetch(retry)
  },
}

let unauthorizedHandler: (() => void) | null = null

/** What to do when the session has ended: the app sends the user to sign in. */
export function onUnauthorized(handler: () => void): void {
  unauthorizedHandler = handler
}

/**
 * A 401 means the portal no longer vouches for the user here: the session ended (expired,
 * or ended elsewhere), or their access to this server was taken away. The app asks the
 * portal which. Sign-in itself answers 401 for a wrong password, and the portal's auth
 * endpoints are the sign-in page's own business, so those are left to the caller.
 */
export const unauthorized: Middleware = {
  onResponse({ request, response }) {
    if (response.status === 401 && !isPortalAuthPath(new URL(request.url, 'http://localhost').pathname)) {
      unauthorizedHandler?.()
    }

    return undefined
  },
}

/** The portal's own sign-in endpoints, at the root; a controller's paths are under the base. */
export function isPortalAuthPath(path: string): boolean {
  return path.startsWith('/api/auth/')
}

export const api = createClient<paths>({ baseUrl: window.location.origin + BASE.replace(/\/$/, ''), credentials: 'same-origin' })
export const portal = createClient<portalPaths>({ baseUrl: window.location.origin, credentials: 'same-origin' })
// openapi-fetch runs onResponse hooks last-registered first. antiforgery goes last so its
// retry runs first, and unauthorized then sees the final response - a retry that ends in
// 401 must still end the session.
api.use(unauthorized, antiforgery)
portal.use(unauthorized, antiforgery)
