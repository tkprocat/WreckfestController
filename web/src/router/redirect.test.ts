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
    for (const target of [
      'https://evil.example',
      '//evil.example',
      String.raw`/\evil.example`,
      '/\t/evil.example',
      '/\n/evil.example',
      // Normalises to "//evil.example".
      '/a/..//evil.example',
      '/./..//evil.example',
      'javascript:alert(1)',
      'admin',
      '',
      undefined,
      ['/admin'],
    ]) {
      expect(redirectTarget(target), JSON.stringify(target)).toBe(AFTER_SIGN_IN)
    }
  })

  // Encoded slashes stay part of one path segment: they do not make a second origin.
  it('keeps an encoded slash inside the app', () => {
    expect(redirectTarget('/%2F%2Fevil.example')).toBe('/%2F%2Fevil.example')
  })

  // Signed in, a way back to /login would loop - in any spelling, since routes match
  // regardless of case and the browser normalises the path.
  it('refuses the sign-in page itself', () => {
    for (const target of ['/login', '/login?redirect=/admin', '/login/', '/LOGIN?redirect=/', '/Login', '/a/../login', '/login\t', '/./login']) {
      expect(redirectTarget(target), JSON.stringify(target)).toBe(AFTER_SIGN_IN)
    }

    expect(redirectTarget('/loginhelp')).toBe('/loginhelp')
  })

  it('answers with the normalised path', () => {
    expect(redirectTarget('/admin/../admin/server')).toBe('/admin/server')
  })
})
