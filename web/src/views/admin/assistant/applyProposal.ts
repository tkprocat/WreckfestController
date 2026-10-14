import { api } from '@/api/client'
import { problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'

/**
 * Applying what the admin AI assistant proposes. The assistant writes nothing: each
 * proposal is the request an admin page would send, and Apply sends it to that page's own
 * endpoint, with the version it was proposed against as If-Match. So it passes the same
 * checks, and a change made since the proposal is a conflict, not overwritten.
 */
export type Proposal = components['schemas']['AssistantProposal']

type Schemas = components['schemas']

export type ApplyOutcome =
  | { ok: true; message: string }
  | { ok: false; message: string; stale: boolean }

const STALE = 'This changed since the assistant proposed it. Ask again for a fresh proposal.'
const NO_ANSWER = 'No answer from the server. Check your connection and try again.'

const ifMatch = (proposal: Proposal) => (proposal.version ? { 'If-Match': `"${proposal.version}"` } : undefined)

/** What a proposal's Apply button does, for its kind. */
export async function applyProposal(proposal: Proposal): Promise<ApplyOutcome> {
  let result: { error?: unknown; response: Response }
  try {
    result = await send(proposal)
  } catch (error) {
    if (error instanceof UnknownKindError) {
      return { ok: false, message: error.message, stale: false }
    }

    return { ok: false, message: NO_ANSWER, stale: false }
  }

  if (result.response.ok) {
    return { ok: true, message: 'Applied.' }
  }

  if (result.response.status === 409 || result.response.status === 412) {
    return { ok: false, message: STALE, stale: true }
  }

  if (result.response.status === 404) {
    return { ok: false, message: 'What this changes no longer exists.', stale: true }
  }

  return { ok: false, message: problemMessage(result.error, 'That could not be applied.'), stale: false }
}

class UnknownKindError extends Error {}

function send(proposal: Proposal) {
  const id = proposal.targetId ?? 0
  const headers = ifMatch(proposal)
  switch (proposal.kind) {
    case 'setRotation':
      return api.PUT('/api/config/tracks', { body: proposal.body as Schemas['UpdateEventLoopTracksRequest'], headers })
    case 'createCollection':
      return api.POST('/api/collections', { body: proposal.body as Schemas['CollectionRequest'] })
    case 'updateCollection':
      return api.PUT('/api/collections/{id}', { params: { path: { id } }, body: proposal.body as Schemas['CollectionRequest'], headers })
    case 'deployCollection':
      return api.POST('/api/collections/{id}/deploy', { params: { path: { id } } })
    case 'createCup':
      return api.POST('/api/cups', { body: proposal.body as Schemas['CupRequest'] })
    case 'updateCup':
      return api.PUT('/api/cups/{id}', { params: { path: { id } }, body: proposal.body as Schemas['CupRequest'], headers })
    case 'updateServerConfig':
      return api.PUT('/api/config/basic', { body: proposal.body })
    default:
      throw new UnknownKindError(`This version of the admin panel cannot apply "${proposal.kind}". Reload the page.`)
  }
}

/** Where to check the result, for a link under an applied proposal. */
export function pageFor(kind: string): { name: string; label: string } | null {
  switch (kind) {
    case 'setRotation':
    case 'deployCollection':
      return { name: 'admin-rotation', label: 'Rotation' }
    case 'createCollection':
    case 'updateCollection':
      return { name: 'admin-collections', label: 'Collections' }
    case 'createCup':
    case 'updateCup':
      return { name: 'admin-cups', label: 'Cups' }
    case 'updateServerConfig':
      return { name: 'admin-config', label: 'Server config' }
    default:
      return null
  }
}
