import { afterEach, describe, expect, it, vi } from 'vitest'
import { antiforgery, onUnauthorized, readCookie, unauthorized, XSRF_HEADER } from './client'

// The middlewares are called as openapi-fetch calls them, with only what they read.
type RequestHook = NonNullable<typeof antiforgery.onRequest>
type ResponseHook = NonNullable<typeof unauthorized.onResponse>
const callRequest = (request: Request) => antiforgery.onRequest!({ request } as Parameters<RequestHook>[0])
const callResponse = (request: Request, response: Response) =>
  unauthorized.onResponse!({ request, response } as Parameters<ResponseHook>[0])

function setCookie(value: string | null) {
  document.cookie = value === null ? 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT' : `XSRF-TOKEN=${value}`
}

afterEach(() => {
  setCookie(null)
  vi.unstubAllGlobals()
})

describe('readCookie', () => {
  it('finds a cookie among others and decodes it', () => {
    expect(readCookie('XSRF-TOKEN', 'a=1; XSRF-TOKEN=abc%3D%3D; b=2')).toBe('abc==')
    expect(readCookie('missing', 'a=1')).toBeNull()
  })
})

describe('antiforgery', () => {
  it('leaves safe requests alone', async () => {
    setCookie('token-1')
    expect(await callRequest(new Request('http://localhost/api/cups'))).toBeUndefined()
  })

  it('copies the cookie into the header on a change', async () => {
    setCookie('token-1')
    const request = (await callRequest(new Request('http://localhost/api/cups', { method: 'POST' }))) as Request
    expect(request.headers.get(XSRF_HEADER)).toBe('token-1')
  })

  it('fetches a token first when there is none', async () => {
    const fetch = vi.fn(async () => {
      setCookie('fresh')
      return new Response(null, { status: 204 })
    })
    vi.stubGlobal('fetch', fetch)

    const request = (await callRequest(new Request('http://localhost/api/cups/1', { method: 'DELETE' }))) as Request

    expect(fetch).toHaveBeenCalledWith('/api/auth/antiforgery', { credentials: 'same-origin' })
    expect(request.headers.get(XSRF_HEADER)).toBe('fresh')
  })
})

describe('unauthorized', () => {
  it('reports an ended session, but not a failed sign-in', () => {
    const handler = vi.fn()
    onUnauthorized(handler)

    callResponse(new Request('http://localhost/api/cups'), new Response(null, { status: 401 }))
    callResponse(new Request('http://localhost/api/auth/login', { method: 'POST' }), new Response(null, { status: 401 }))
    callResponse(new Request('http://localhost/api/cups'), new Response(null, { status: 403 }))

    expect(handler).toHaveBeenCalledTimes(1)
  })
})
