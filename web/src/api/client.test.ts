import { afterEach, describe, expect, it, vi } from 'vitest'
import { ANTIFORGERY_FAILURE, antiforgery, onUnauthorized, readCookie, unauthorized, XSRF_HEADER } from './client'

// The middlewares are called as openapi-fetch calls them, with only what they read.
type RequestHook = NonNullable<typeof antiforgery.onRequest>
type ResponseHook = NonNullable<typeof unauthorized.onResponse>
const callRequest = (request: Request) => antiforgery.onRequest!({ request } as Parameters<RequestHook>[0])
const callResponse = (request: Request, response: Response) =>
  unauthorized.onResponse!({ request, response } as Parameters<ResponseHook>[0])
const callAntiforgeryResponse = (request: Request, response: Response) =>
  antiforgery.onResponse!({ request, response } as Parameters<ResponseHook>[0])

const antiforgeryFailure = () =>
  new Response(JSON.stringify({ title: ANTIFORGERY_FAILURE, status: 400 }), {
    status: 400,
    headers: { 'Content-Type': 'application/problem+json' },
  })

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

  // A rejected or failed token fetch must not be kept and handed to every later request.
  it('does not keep a failed token fetch', async () => {
    const fetch = vi
      .fn()
      .mockRejectedValueOnce(new TypeError('offline'))
      .mockResolvedValueOnce(new Response(null, { status: 503 }))
      .mockImplementationOnce(async () => {
        setCookie('fresh')
        return new Response(null, { status: 204 })
      })
    vi.stubGlobal('fetch', fetch)
    const post = () => callRequest(new Request('http://localhost/api/cups', { method: 'POST' }))

    await expect(post()).rejects.toThrow('offline')
    await expect(post()).rejects.toThrow('HTTP 503')
    const request = (await post()) as Request

    expect(fetch).toHaveBeenCalledTimes(3)
    expect(request.headers.get(XSRF_HEADER)).toBe('fresh')
  })

  // A session that ended leaves the old user's token in the cookie, and a cookie that is
  // present is never refetched: without a retry, signing in again fails forever.
  it('gets a fresh token and sends the request once more when the server refuses the token', async () => {
    setCookie('stale')
    const sent = (await callRequest(
      new Request('http://localhost/api/auth/login', { method: 'POST', body: '{"login":"admin"}' }),
    )) as Request
    const fetch = vi.fn(async (input: RequestInfo | URL) => {
      if (input === '/api/auth/antiforgery') {
        setCookie('fresh')
        return new Response(null, { status: 204 })
      }

      const retry = input as Request
      expect(retry.headers.get(XSRF_HEADER)).toBe('fresh')
      expect(await retry.text()).toBe('{"login":"admin"}')
      return new Response('{"userName":"admin"}', { status: 200 })
    })
    vi.stubGlobal('fetch', fetch)

    const response = (await callAntiforgeryResponse(sent, antiforgeryFailure())) as Response

    expect(response.status).toBe(200)
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('does not retry other 400s, or a request it did not send', async () => {
    setCookie('token-1')
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    const sent = (await callRequest(new Request('http://localhost/api/cups', { method: 'POST', body: '{}' }))) as Request

    const other = new Response(JSON.stringify({ title: 'One or more validation errors occurred.' }), { status: 400 })
    expect(await callAntiforgeryResponse(sent, other)).toBeUndefined()
    expect(await callAntiforgeryResponse(new Request('http://localhost/api/cups', { method: 'POST' }), antiforgeryFailure()))
      .toBeUndefined()
    expect(fetch).not.toHaveBeenCalled()
  })

  // The retry goes straight to fetch, past the middleware, so a second refusal is final.
  it('retries only once', async () => {
    setCookie('stale')
    const sent = (await callRequest(new Request('http://localhost/api/cups', { method: 'POST', body: '{}' }))) as Request
    const fetch = vi.fn(async (input: RequestInfo | URL) => {
      if (input === '/api/auth/antiforgery') {
        setCookie('fresh')
        return new Response(null, { status: 204 })
      }

      return antiforgeryFailure()
    })
    vi.stubGlobal('fetch', fetch)

    const response = (await callAntiforgeryResponse(sent, antiforgeryFailure())) as Response

    expect(response.status).toBe(400)
    expect(fetch).toHaveBeenCalledTimes(2)
    expect(await callAntiforgeryResponse(sent, antiforgeryFailure())).toBeUndefined()
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
