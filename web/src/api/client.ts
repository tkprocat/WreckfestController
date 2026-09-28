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

let tokenRequest: Promise<void> | null = null

/**
 * Asks the server for a fresh antiforgery token (it arrives as a cookie). The token is
 * tied to who is signed in, so call this after signing in or out.
 */
export function refreshAntiforgery(): Promise<void> {
  tokenRequest = fetch('/api/auth/antiforgery', { credentials: 'same-origin' }).then(() => undefined)
  return tokenRequest
}

/** Copies the antiforgery cookie into the header on every request that changes something. */
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

    return request
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
api.use(antiforgery, unauthorized)
