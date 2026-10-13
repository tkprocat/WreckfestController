import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NMessageProvider } from 'naive-ui'
import AssistantSettings from './AssistantSettings.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), PUT: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const answer = (data: unknown, status = 200) => ({ data, error: undefined, response: new Response(null, { status }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

const settings = (overrides: Record<string, unknown> = {}) => ({
  enabled: false,
  baseUrl: 'https://api.openai.com/v1',
  model: 'gpt-5-mini',
  apiKeySet: false,
  dailyRequestLimit: 200,
  version: 2,
  ...overrides,
})

let wrapper: VueWrapper | undefined

async function mountCard() {
  wrapper = mount(defineComponent({ render: () => h(NMessageProvider, () => h(AssistantSettings)) }), { attachTo: document.body })
  await flushPromises()
  return wrapper
}

const button = (label: string) => wrapper!.findAll('button').find((b) => b.text().trim() === label)
const input = (label: string) => wrapper!.find(`input[aria-label="${label}"]`)

async function save() {
  await button('Save')!.trigger('click')
  await flushPromises()
}

beforeEach(() => {
  api.GET.mockReset()
  api.PUT.mockReset()
  api.GET.mockImplementation((path: string) =>
    Promise.resolve(path === '/api/settings/assistant' ? answer(settings()) : answer({ enabled: false })),
  )
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('AssistantSettings', () => {
  it('sends only what changed, the key included, with the loaded version as If-Match', async () => {
    api.PUT.mockResolvedValue(answer(settings({ enabled: true, apiKeySet: true, model: 'gpt-test', version: 3 })))
    await mountCard()

    expect(button('Save')!.attributes('disabled')).toBeDefined()
    await wrapper!.find('[aria-label="Show the AI page"]').trigger('click')
    await input('API key').setValue('sk-new')
    await input('Model').setValue('gpt-test')
    await save()

    expect(api.PUT).toHaveBeenCalledWith('/api/settings/assistant', {
      body: { enabled: true, model: 'gpt-test', apiKey: 'sk-new' },
      headers: { 'If-Match': '"2"' },
    })
    expect(document.body.textContent).toContain('AI assistant settings saved.')
    expect((input('API key').element as HTMLInputElement).value).toBe('')
    expect(document.body.textContent).toContain('A key is set.')
  })

  it('never sends the key unless one is typed', async () => {
    api.GET.mockResolvedValue(answer(settings({ apiKeySet: true })))
    api.PUT.mockResolvedValue(answer(settings({ apiKeySet: true, dailyRequestLimit: 50, version: 3 })))
    await mountCard()

    await input('Questions per day').setValue('50')
    await input('Questions per day').trigger('blur')
    await save()

    expect(api.PUT).toHaveBeenCalledWith('/api/settings/assistant', {
      body: { dailyRequestLimit: 50 },
      headers: { 'If-Match': '"2"' },
    })
  })

  it('removes the key with an empty one', async () => {
    api.GET.mockResolvedValue(answer(settings({ apiKeySet: true })))
    api.PUT.mockResolvedValue(answer(settings({ version: 3 })))
    await mountCard()

    await button('Remove key')!.trigger('click')
    expect(document.body.textContent).toContain('The key will be removed when you save.')
    await save()

    expect(api.PUT).toHaveBeenCalledWith('/api/settings/assistant', {
      body: { apiKey: '' },
      headers: { 'If-Match': '"2"' },
    })
  })

  it('shows a field error under the field', async () => {
    api.PUT.mockResolvedValue(
      refused({ title: 'One or more validation errors occurred.', errors: { baseUrl: ['baseUrl must be an http or https address.'] } }, 400),
    )
    await mountCard()

    await input('API address').setValue('ftp://example.com')
    await save()

    expect(document.body.textContent).toContain('baseUrl must be an http or https address.')
  })

  it('reloads and says so when the settings were changed elsewhere', async () => {
    api.PUT.mockResolvedValue(refused(settings({ model: 'other-model', version: 3 }), 409))
    await mountCard()

    await input('Model').setValue('gpt-test')
    await save()

    expect(document.body.textContent).toContain('changed elsewhere')
    expect((input('Model').element as HTMLInputElement).value).toBe('other-model')
  })
})
