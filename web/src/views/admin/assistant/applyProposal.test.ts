import { beforeEach, describe, expect, it, vi } from 'vitest'
import { applyProposal, type Proposal } from './applyProposal'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const done = (status = 200) => ({ data: {}, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

const proposal = (kind: string, extra: Partial<Proposal> = {}): Proposal => ({
  kind,
  summary: 'A change',
  targetId: null,
  version: null,
  body: { name: 'Ovals' },
  ...extra,
})

beforeEach(() => {
  api.POST.mockReset().mockResolvedValue(done())
  api.PUT.mockReset().mockResolvedValue(done())
})

describe('applyProposal', () => {
  it.each([
    ['setRotation', 'PUT', '/api/config/tracks', { version: 'abc' }, { body: { name: 'Ovals' }, headers: { 'If-Match': '"abc"' } }],
    ['createCollection', 'POST', '/api/collections', {}, { body: { name: 'Ovals' } }],
    ['updateCollection', 'PUT', '/api/collections/{id}', { targetId: 4, version: '2' },
      { params: { path: { id: 4 } }, body: { name: 'Ovals' }, headers: { 'If-Match': '"2"' } }],
    ['deployCollection', 'POST', '/api/collections/{id}/deploy', { targetId: 4, body: null }, { params: { path: { id: 4 } } }],
    ['createCup', 'POST', '/api/cups', {}, { body: { name: 'Ovals' } }],
    ['updateCup', 'PUT', '/api/cups/{id}', { targetId: 7, version: '3' },
      { params: { path: { id: 7 } }, body: { name: 'Ovals' }, headers: { 'If-Match': '"3"' } }],
    ['updateServerConfig', 'PUT', '/api/config/basic', {}, { body: { name: 'Ovals' } }],
  ] as const)('sends %s to its admin endpoint', async (kind, method, path, extra, options) => {
    const outcome = await applyProposal(proposal(kind, extra as Partial<Proposal>))

    expect(outcome).toEqual({ ok: true, message: 'Applied.' })
    expect(api[method]).toHaveBeenCalledWith(path, options)
  })

  it('reports a change made since as stale', async () => {
    api.PUT.mockResolvedValue(refused({ title: 'Conflict' }, 409))

    const outcome = await applyProposal(proposal('updateCup', { targetId: 7, version: '3' }))

    expect(outcome).toMatchObject({ ok: false, stale: true })
    expect(outcome.message).toContain('Ask again')
  })

  it("passes on the endpoint's reason for a refusal", async () => {
    api.PUT.mockResolvedValue(refused({ errors: { maxPlayers: ['maxPlayers must be a number.'] } }, 400))

    const outcome = await applyProposal(proposal('updateServerConfig'))

    expect(outcome).toEqual({ ok: false, message: 'maxPlayers must be a number.', stale: false })
  })

  it('refuses a kind it does not know without sending anything', async () => {
    const outcome = await applyProposal(proposal('deleteEverything'))

    expect(outcome.ok).toBe(false)
    expect(api.POST).not.toHaveBeenCalled()
    expect(api.PUT).not.toHaveBeenCalled()
  })

  it('says when there is no answer', async () => {
    api.POST.mockRejectedValue(new TypeError('Failed to fetch'))

    const outcome = await applyProposal(proposal('createCup'))

    expect(outcome).toMatchObject({ ok: false, stale: false })
    expect(outcome.message).toContain('No answer from the server.')
  })
})
