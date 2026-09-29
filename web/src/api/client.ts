import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'

// The typed client for the controller's API. Types come from src/api/schema.d.ts,
// generated from src/api/openapi.json, which a C# test keeps in step with the API.

/** The antiforgery cookie the server sets, and the header it expects the token back in. */
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

/** The title of the server's 400 for a bad token (Services/Auth/CookieAntiforgeryFilter.cs). */
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

    await refreshAntiforgery()
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
 * A 401 means the session ended (expired, or ended from another device). Sign-in itself
 * answers 401 for a wrong password, and the auth endpoints are the sign-in page's own
 * business, so those are left to the caller.
 */
export const unauthorized: Middleware = {
  onResponse({ request, response }) {
    if (response.status === 401 && !new URL(request.url, 'http://localhost').pathname.startsWith('/api/auth/')) {
      unauthorizedHandler?.()
    }

    return undefined
  },
}

export const api = createClient<paths>({ baseUrl: window.location.origin, credentials: 'same-origin' })
// openapi-fetch runs onResponse hooks last-registered first. antiforgery goes last so its
// retry runs first, and unauthorized then sees the final response - a retry that ends in
// 401 must still end the session.
api.use(unauthorized, antiforgery)
