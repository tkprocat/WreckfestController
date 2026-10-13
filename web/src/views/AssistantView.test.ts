import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import AssistantView from './AssistantView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

let wrapper: VueWrapper | undefined

async function mountPage() {
  wrapper = mount(AssistantView, { attachTo: document.body })
  await flushPromises()
  return wrapper
}

const button = (label: string) => wrapper!.findAll('button').find((b) => b.text().trim() === label)
const box = () => wrapper!.find('textarea[aria-label="Your question"]')

async function type(text: string) {
  await box().setValue(text)
}

beforeEach(() => {
  api.GET.mockReset()
  api.POST.mockReset()
  api.GET.mockResolvedValue(answer({ enabled: true }))
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('AssistantView', () => {
  it('says the assistant is off when it is', async () => {
    api.GET.mockResolvedValue(answer({ enabled: false }))
    await mountPage()

    expect(api.GET).toHaveBeenCalledWith('/api/public/assistant')
    expect(wrapper!.text()).toContain('The AI assistant is turned off.')
    expect(wrapper!.find('textarea').exists()).toBe(false)
  })

  it('sends the conversation so far and shows the answer', async () => {
    api.POST.mockResolvedValueOnce(answer({ reply: 'Two players are on.' }))
    api.POST.mockResolvedValueOnce(answer({ reply: 'Big Valley.' }))
    await mountPage()

    await type('Who is online?')
    await wrapper!.find('form').trigger('submit')
    await flushPromises()
    await type('And the track?')
    await wrapper!.find('form').trigger('submit')
    await flushPromises()

    expect(api.POST).toHaveBeenNthCalledWith(1, '/api/public/assistant/chat', {
      body: { messages: [{ role: 'user', content: 'Who is online?' }] },
    })
    expect(api.POST).toHaveBeenNthCalledWith(2, '/api/public/assistant/chat', {
      body: {
        messages: [
          { role: 'user', content: 'Who is online?' },
          { role: 'assistant', content: 'Two players are on.' },
          { role: 'user', content: 'And the track?' },
        ],
      },
    })
    const log = wrapper!.find('[aria-label="Conversation"]').text()
    expect(log).toContain('Two players are on.')
    expect(log).toContain('Big Valley.')
    expect((box().element as HTMLTextAreaElement).value).toBe('')
  })

  it('asks a suggested question in one click', async () => {
    api.POST.mockResolvedValue(answer({ reply: 'Nobody yet.' }))
    await mountPage()

    await button('Who is online right now?')!.trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/public/assistant/chat', {
      body: { messages: [{ role: 'user', content: 'Who is online right now?' }] },
    })
    expect(wrapper!.text()).toContain('Nobody yet.')
  })

  it("formats an answer's Markdown, but never its HTML", async () => {
    api.POST.mockResolvedValue(answer({ reply: '<img src=x onerror=alert(1)> **bold**' }))
    await mountPage()

    await type('Hi')
    await wrapper!.find('form').trigger('submit')
    await flushPromises()

    const log = wrapper!.find('[aria-label="Conversation"]')
    expect(log.find('img').exists()).toBe(false)
    expect(log.find('strong').text()).toBe('bold')
    expect(log.text()).toContain('<img src=x onerror=alert(1)>')
  })

  it("keeps the visitor's own question as typed", async () => {
    api.POST.mockResolvedValue(answer({ reply: 'Yes.' }))
    await mountPage()

    await type('Is **this** bold?')
    await wrapper!.find('form').trigger('submit')
    await flushPromises()

    const question = wrapper!.find('[aria-label="Conversation"] .message.user')
    expect(question.find('strong').exists()).toBe(false)
    expect(question.text()).toContain('Is **this** bold?')
  })

  it('puts an unanswered question back in the box with the reason', async () => {
    api.POST.mockResolvedValue(refused({ title: 'The AI assistant has answered all the questions it can for today. Try again tomorrow.' }, 429))
    await mountPage()

    await type('Who won?')
    await wrapper!.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper!.text()).toContain('answered all the questions it can for today')
    expect((box().element as HTMLTextAreaElement).value).toBe('Who won?')
    expect(wrapper!.find('[aria-label="Conversation"]').exists()).toBe(false)
  })

  it('says when there is no answer at all', async () => {
    api.POST.mockRejectedValue(new TypeError('Failed to fetch'))
    await mountPage()

    await type('Hello?')
    await wrapper!.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper!.text()).toContain('No answer from the server.')
    expect((box().element as HTMLTextAreaElement).value).toBe('Hello?')
  })

  it('sends on Enter but not on Shift+Enter', async () => {
    api.POST.mockResolvedValue(answer({ reply: 'Yes.' }))
    await mountPage()

    await type('Line one')
    await box().trigger('keydown', { key: 'Enter', shiftKey: true })
    await flushPromises()
    expect(api.POST).not.toHaveBeenCalled()

    await box().trigger('keydown', { key: 'Enter' })
    await flushPromises()
    expect(api.POST).toHaveBeenCalledTimes(1)
  })
})
