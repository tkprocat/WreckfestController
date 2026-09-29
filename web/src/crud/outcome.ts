import { useDialog } from 'naive-ui'
import { fieldErrors, problemMessage } from '@/api/problems'

/** A request that got no answer: whether it happened is unknown until the page reloads. */
export const NO_ANSWER = 'No answer from the controller, so it is not known whether this happened. Reload before trying again.'

/**
 * What a change to the server came to. Every save and action on a resource page ends in
 * one of these, so each page handles the same cases the same way.
 */
export type Outcome<T> =
  /** Done. `row` is the server's answer when it sent one (not for a 204). */
  | { kind: 'ok'; row?: T }
  /** A 400: per-field messages, keyed as the request named the fields. */
  | { kind: 'invalid'; errors: Record<string, string>; message: string }
  /** A 409 that carries the row as it is now: someone else changed it meanwhile. */
  | { kind: 'conflict'; current: T }
  /** Any other refusal (a 409 problem with a reason, a 404, a 5xx): its title. */
  | { kind: 'refused'; message: string }
  /** The request threw: it may or may not have reached the server. */
  | { kind: 'no-answer' }

interface Answer<T> {
  data?: T
  error?: unknown
  response: Response
}

/**
 * Runs a request and says what it came to. `isRow` tells a 409 that carries the current
 * row (a version conflict) from a 409 problem (a refused rule).
 */
export async function send<T>(
  request: () => Promise<Answer<T>>,
  isRow: (body: unknown) => body is T,
  fallback: string,
): Promise<Outcome<T>> {
  let answer: Answer<T>
  try {
    answer = await request()
  } catch {
    return { kind: 'no-answer' }
  }

  const { data, error, response } = answer
  if (response.ok) {
    return { kind: 'ok', row: data }
  }

  if (response.status === 400) {
    return { kind: 'invalid', errors: fieldErrors(error), message: problemMessage(error, fallback) }
  }

  if (response.status === 409 && isRow(error)) {
    return { kind: 'conflict', current: error }
  }

  return { kind: 'refused', message: problemMessage(error, fallback) }
}

/** A row the API returns: it has an id, and a version when it is versioned. */
export function hasId(body: unknown): body is { id: unknown } {
  return typeof body === 'object' && body !== null && 'id' in body
}

/**
 * Asks before something that cannot be undone. Resolves true when confirmed; the dialog
 * the button raises is named by its title.
 */
export function useConfirm() {
  const dialog = useDialog()
  return (options: { title: string; content: string; positive: string }) =>
    new Promise<boolean>((resolve) => {
      dialog.warning({
        title: options.title,
        content: options.content,
        positiveText: options.positive,
        negativeText: 'Cancel',
        onPositiveClick: () => resolve(true),
        onNegativeClick: () => resolve(false),
        onClose: () => resolve(false),
        onMaskClick: () => resolve(false),
      })
    })
}
