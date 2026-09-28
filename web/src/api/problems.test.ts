import { describe, expect, it } from 'vitest'
import { fieldErrors, problemMessage } from './problems'

describe('problemMessage', () => {
  it('prefers a field message, then the title, then the fallback', () => {
    expect(problemMessage({ errors: { maxLapsAllowed: ['maxLapsAllowed must be between 1 and 999.'] } }, 'x')).toBe(
      'maxLapsAllowed must be between 1 and 999.',
    )
    expect(problemMessage({ title: 'Server is not running' }, 'x')).toBe('Server is not running')
    expect(problemMessage(undefined, 'Could not start the server.')).toBe('Could not start the server.')
  })
})

describe('fieldErrors', () => {
  it('keeps the first message per field', () => {
    expect(fieldErrors({ errors: { mode: ['one', 'two'] } })).toEqual({ mode: 'one' })
    expect(fieldErrors({ title: 'no fields' })).toEqual({})
  })
})
