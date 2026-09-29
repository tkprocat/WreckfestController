/** Where sign-in goes when nothing sent the user there. */
export const AFTER_SIGN_IN = '/admin'

/** A stand-in origin to resolve against: a target that leaves it is not inside the app. */
const APP = 'http://app.invalid'

/**
 * Where to go after signing in: the `redirect` query value, but only a path inside the
 * app, so a crafted link cannot send the user elsewhere. The value is normalised the way
 * a browser would read it - tabs and newlines dropped, "../" resolved, "/\host" read as
 * "//host" - and refused when that leaves the app or lands on the sign-in page itself
 * (in any spelling: routes match regardless of case), which would loop.
 */
export function redirectTarget(redirect: unknown): string {
  if (typeof redirect !== 'string' || !redirect.startsWith('/') || redirect.startsWith('//') || redirect.includes('\\')) {
    return AFTER_SIGN_IN
  }

  let url: URL
  try {
    url = new URL(redirect, APP)
  } catch {
    return AFTER_SIGN_IN
  }

  // The normalised path is checked again: "/a/..//host" stays on this origin while it is
  // resolved here, but normalises to "//host", which the browser would read as another site.
  if (url.origin !== APP || url.pathname.startsWith('//') || /^\/login\/*$/i.test(url.pathname)) {
    return AFTER_SIGN_IN
  }

  return url.pathname + url.search + url.hash
}
