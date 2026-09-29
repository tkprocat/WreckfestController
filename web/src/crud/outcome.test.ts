import { describe, expect, it } from 'vitest'
import { hasId, send } from './outcome'

const reply = (status: number, data?: unknown, error?: unknown) => async () => ({
  data,
  error,
  response: new Response(null, { status }),
})
const isRow = (body: unknown): body is { id: number; version: number } => hasId(body) && 'version' in body

describe('send', () => {
  it('is ok with the row the server answered with, or none for a 204', async () => {
    expect(await send(reply(200, { id: 1, version: 2 }), isRow, 'x')).toEqual({ kind: 'ok', row: { id: 1, version: 2 } })
    expect(await send(reply(204), isRow, 'x')).toEqual({ kind: 'ok', row: undefined })
  })

  it('maps a 400 onto the fields it names', async () => {
    const outcome = await send(reply(400, undefined, { title: 'Invalid', errors: { slug: ['Taken.'] } }), isRow, 'x')

    expect(outcome).toEqual({ kind: 'invalid', errors: { slug: 'Taken.' }, message: 'Taken.' })
  })

  // A 409 that carries the row is a version conflict; one with a problem is a refusal.
  it('tells a version conflict from a refused rule', async () => {
    const current = { id: 1, version: 5 }

    expect(await send(reply(409, undefined, current), isRow, 'x')).toEqual({ kind: 'conflict', current })
    expect(await send(reply(409, undefined, { title: 'Built-in tracks cannot be deleted.' }), isRow, 'x')).toEqual({
      kind: 'refused',
      message: 'Built-in tracks cannot be deleted.',
    })
  })

  it('refuses anything else with its title, or the fallback', async () => {
    expect(await send(reply(404), isRow, 'Not saved.')).toEqual({ kind: 'refused', message: 'Not saved.' })
  })

  // Thrown, not answered: it may or may not have reached the server.
  it('says when there was no answer', async () => {
    const outcome = await send(async () => Promise.reject(new TypeError('Failed to fetch')), isRow, 'x')

    expect(outcome).toEqual({ kind: 'no-answer' })
  })
})
