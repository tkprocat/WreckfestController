// The API's error bodies: ProblemDetails ({ title }) for a refusal, and
// ValidationProblemDetails ({ errors: { field: [message] } }) for a bad request.

interface ProblemBody {
  title?: string | null
  detail?: string | null
  errors?: Record<string, string[]>
}

/** Per-field messages from a 400, keyed as the request named the fields. */
export function fieldErrors(error: unknown): Record<string, string> {
  const errors = (error as ProblemBody | undefined)?.errors
  if (!errors || typeof errors !== 'object') {
    return {}
  }

  return Object.fromEntries(Object.entries(errors).map(([field, messages]) => [field, messages[0] ?? '']))
}

/** One line for a toast: the first field message, else the problem's title, else the fallback. */
export function problemMessage(error: unknown, fallback: string): string {
  const body = error as ProblemBody | undefined
  const first = Object.values(fieldErrors(error))[0]
  return first || body?.title || body?.detail || fallback
}
