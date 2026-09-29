import { describe, expect, it } from 'vitest'
import { AFTER_SIGN_IN, redirectTarget } from './redirect'

describe('redirectTarget', () => {
  it('keeps a path inside the app', () => {
    expect(redirectTarget('/admin/server')).toBe('/admin/server')
    expect(redirectTarget('/admin/settings?tab=vote#top')).toBe('/admin/settings?tab=vote#top')
  })

  // A crafted link must not send the user to another site after signing in.
  it('refuses anything that could leave the app', () => {
    // String.raw: a real backslash, as in the link "/\evil.example".
    for (const target of ['https://evil.example', '//evil.example', String.raw`/\evil.example`, 'admin', '', undefined, ['/admin']]) {
      expect(redirectTarget(target), String(target)).toBe(AFTER_SIGN_IN)
    }
  })

  // Signed in, a way back to /login would loop.
  it('refuses the sign-in page itself', () => {
    for (const target of ['/login', '/login?redirect=/admin', '/login/']) {
      expect(redirectTarget(target), target).toBe(AFTER_SIGN_IN)
    }

    expect(redirectTarget('/loginhelp')).toBe('/loginhelp')
  })
})
