/** Where sign-in goes when nothing sent the user there. */
export const AFTER_SIGN_IN = '/admin'

/**
 * Where to go after signing in: the `redirect` query value, but only a path inside the
 * app, so a crafted link cannot send the user elsewhere. "//host" and anything with a
 * backslash are refused (browsers read "/\host" like "//host"), and so is a way back to
 * the sign-in page itself.
 */
export function redirectTarget(redirect: unknown): string {
  return typeof redirect === 'string' &&
    redirect.startsWith('/') &&
    !redirect.startsWith('//') &&
    !redirect.includes('\\') &&
    !/^\/login(?:[/?#]|$)/.test(redirect)
    ? redirect
    : AFTER_SIGN_IN
}
