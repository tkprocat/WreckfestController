import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub, type VueWrapper } from '@vue/test-utils'
import AdminAssistantView from './AdminAssistantView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn(), PUT: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

const rotation = {
  kind: 'setRotation',
  summary: 'Ovals only',
  targetId: null,
  version: 'v1',
  body: { collectionName: 'Ovals', tracks: [{ track: 'speedway2_figure_8' }] },
}

let wrapper: VueWrapper | undefined

async function mountPage() {
  wrapper = mount(AdminAssistantView, { attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
  await flushPromises()
  return wrapper
}

const button = (label: string) => wrapper!.findAll('button').find((b) => b.text().trim() === label)
const box = () => wrapper!.find('textarea[aria-label="Your question"]')

async function ask(text: string) {
  await box().setValue(text)
  await wrapper!.find('form').trigger('submit')
  await flushPromises()
}

beforeEach(() => {
  api.GET.mockReset().mockResolvedValue(answer({ available: true }))
  api.POST.mockReset()
  api.PUT.mockReset()
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('AdminAssistantView', () => {
  it('says where the key is set when there is none', async () => {
    api.GET.mockResolvedValue(answer({ available: false }))
    await mountPage()

    expect(api.GET).toHaveBeenCalledWith('/api/assistant')
    expect(wrapper!.text()).toContain('The AI assistant has no API key.')
    expect(wrapper!.text()).toContain('user-settings.json')
    expect(wrapper!.find('textarea').exists()).toBe(false)
  })

  it('shows a proposal, and changes nothing until it is applied', async () => {
    api.POST.mockResolvedValue(answer({ reply: 'Here is an ovals-only rotation.', proposals: [rotation] }))
    api.PUT.mockResolvedValue(answer({}))
    await mountPage()

    await ask('Ovals only, please.')

    expect(api.POST).toHaveBeenCalledWith('/api/assistant/chat', {
      body: { messages: [{ role: 'user', content: 'Ovals only, please.' }] },
    })
    const proposals = wrapper!.find('[aria-label="Proposed changes"]')
    expect(proposals.text()).toContain('Ovals only')
    expect(api.PUT).not.toHaveBeenCalled()

    await button('Apply')!.trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/config/tracks', { body: rotation.body, headers: { 'If-Match': '"v1"' } })
    expect(proposals.text()).toContain('Applied')
    expect(button('Apply')).toBeUndefined()
  })

  it('tells the model what was applied when the admin asks again', async () => {
    api.POST.mockResolvedValueOnce(answer({ reply: 'Proposed.', proposals: [rotation, { ...rotation, summary: 'Also this' }] }))
    api.POST.mockResolvedValueOnce(answer({ reply: 'Done.', proposals: [] }))
    api.PUT.mockResolvedValue(answer({}))
    await mountPage()

    await ask('Ovals only.')
    await button('Apply')!.trigger('click')
    await flushPromises()
    await ask('Thanks. Anything else?')

    const messages = api.POST.mock.calls[1]![1].body.messages
    expect(messages[1]).toEqual({
      role: 'assistant',
      content: 'Proposed.\n\nProposed:\n- Ovals only (applied)\n- Also this (not applied)',
    })
  })

  it('says why a proposal could not be applied, and lets the admin try again', async () => {
    api.POST.mockResolvedValue(answer({ reply: 'Proposed.', proposals: [rotation] }))
    api.PUT.mockResolvedValue(refused({ title: 'Conflict' }, 409))
    await mountPage()

    await ask('Ovals only.')
    await button('Apply')!.trigger('click')
    await flushPromises()

    expect(wrapper!.find('[role="alert"]').text()).toContain('Ask again')
    expect(button('Try again')).toBeDefined()
  })

  it('puts an unanswered question back in the box with the reason', async () => {
    api.POST.mockResolvedValue(refused({ title: 'The AI assistant cannot answer right now.' }, 503))
    await mountPage()

    await ask('Who won?')

    expect(wrapper!.text()).toContain('The AI assistant cannot answer right now.')
    expect((box().element as HTMLTextAreaElement).value).toBe('Who won?')
    expect(wrapper!.find('[aria-label="Conversation"]').exists()).toBe(false)
  })
})
