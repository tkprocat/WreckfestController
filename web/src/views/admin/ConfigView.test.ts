import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NMessageProvider } from 'naive-ui'
import ConfigView from './ConfigView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), PUT: vi.fn() }))
vi.mock('@/api/client', () => ({ api }))

const config = (overrides: Record<string, unknown> = {}) => ({
  serverName: 'Race night',
  welcomeMessage: '',
  password: '',
  maxPlayers: 24,
  lan: 0,
  excludeFromQuickplay: 0,
  adminSteamIds: '',
  laps: 3,
  sessionMode: 'normal',
  ...overrides,
})

const fields = [
  { field: 'serverName', key: 'server_name', savable: true, reason: null },
  { field: 'maxPlayers', key: 'max_players', savable: true, reason: null },
  { field: 'lan', key: 'lan', savable: true, reason: null },
  { field: 'adminSteamIds', key: 'admin_steam_ids', savable: false, reason: 'no active line; add or uncomment it' },
]

const answer = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) })
const refused = (error: unknown, status: number) => ({ data: undefined, error, response: new Response(null, { status }) })

function serve(current = config()) {
  api.GET.mockImplementation((path: string) =>
    Promise.resolve(
      {
        '/api/config/basic': answer(current),
        '/api/config/basic/fields': answer(fields),
        '/api/config/tracks': answer({ count: 1, tracks: [{ track: 'fields14', laps: 5 }] }),
        '/api/config/tracks/collection-name': answer({ collectionName: 'Evening' }),
      }[path],
    ),
  )
}

let wrapper: VueWrapper | undefined

function mountPage() {
  wrapper = mount(defineComponent({ render: () => h(NMessageProvider, () => h(ConfigView)) }), { attachTo: document.body })
  return wrapper
}

const input = (label: string) => wrapper!.find(`input[aria-label="${label}"]`)
const button = (label: string) => wrapper!.findAll('button').find((b) => b.text().trim() === label)!

beforeEach(() => {
  api.GET.mockReset()
  api.PUT.mockReset()
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('ConfigView', () => {
  it('saves only the changed settings, and shows what the file now holds', async () => {
    serve()
    api.PUT.mockResolvedValue(answer(config({ maxPlayers: 20 })))
    mountPage()
    await flushPromises()

    await input('Max players').setValue('20')
    await input('Max players').trigger('blur')
    await button('Save').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/config/basic', { body: { maxPlayers: 20 } })
    expect(document.body.textContent).toContain('Server settings saved.')
  })

  // A setting server_config.cfg has no active line for cannot be saved: greyed out, with
  // what to change in the file, instead of failing on Save.
  it('greys out a setting the file cannot take, and says why', async () => {
    serve()
    mountPage()
    await flushPromises()

    expect(input('Admin Steam IDs').attributes('disabled')).toBeDefined()
    expect(wrapper!.text()).toContain('no active line; add or uncomment it')
    expect(input('Server name').attributes('disabled')).toBeUndefined()
  })

  // 0/1 settings are switches: flipping one sends 1 or 0, not true or false.
  it('saves a switch as 1 or 0', async () => {
    serve()
    api.PUT.mockResolvedValue(answer(config({ lan: 1 })))
    mountPage()
    await flushPromises()

    await wrapper!.find('[aria-label="LAN only"]').trigger('click')
    await button('Save').trigger('click')
    await flushPromises()

    expect(api.PUT).toHaveBeenCalledWith('/api/config/basic', { body: { lan: 1 } })
  })

  it('shows a refusal from the server', async () => {
    serve()
    api.PUT.mockResolvedValue(refused({ title: 'server_config.cfg cannot take this change: laps (set again below).' }, 409))
    mountPage()
    await flushPromises()

    await input('Server name').setValue('New name')
    await button('Save').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('cannot take this change')
  })

  // The preview shows the file as it would be, unsaved changes and the rotation included.
  it('previews unsaved changes with the rotation', async () => {
    serve()
    mountPage()
    await flushPromises()

    await input('Server name').setValue('Changed name')
    // Tab panes render when first opened.
    await wrapper!.findAll('.n-tabs-tab').find((tab) => tab.text().trim() === 'Preview')!.trigger('click')
    await flushPromises()

    const preview = wrapper!.find('pre.preview')
    expect(preview.text()).toContain('server_name=Changed name')
    expect(preview.text()).toContain('#CollectionName Evening')
    expect(preview.text()).toContain('el_add=fields14')
    // Not active in the file: shown commented out, as it is there.
    expect(preview.text()).toContain('#admin_steam_ids=')
  })

  // Each control's own disabled overrides the form's, so each must lock itself: the
  // answer replaces the form, and an edit made while saving would be lost.
  it('locks every control while saving', async () => {
    serve()
    let finish!: (value: unknown) => void
    api.PUT.mockReturnValue(new Promise((resolve) => (finish = resolve)))
    mountPage()
    await flushPromises()

    await input('Server name').setValue('New name')
    await button('Save').trigger('click')
    await flushPromises()
    expect(input('Server name').attributes('disabled')).toBeDefined()
    expect(input('Max players').attributes('disabled')).toBeDefined()

    finish(answer(config({ serverName: 'New name' })))
    await flushPromises()
    expect(input('Server name').attributes('disabled')).toBeUndefined()
  })

  // Without knowing which settings the file can take, the page does not guess.
  it('turns editing off when it cannot tell which settings can be saved', async () => {
    serve()
    const answers = api.GET.getMockImplementation()!
    api.GET.mockImplementation((path: string) =>
      path === '/api/config/basic/fields' ? Promise.resolve(refused({ title: 'Database unavailable.' }, 503)) : answers(path),
    )
    mountPage()
    await flushPromises()

    expect(wrapper!.text()).toContain('Editing is off')
    expect(input('Server name').attributes('disabled')).toBeDefined()
    expect(input('Max players').attributes('disabled')).toBeDefined()

    // Nor does the preview guess which settings are active.
    await wrapper!.findAll('.n-tabs-tab').find((tab) => tab.text().trim() === 'Preview')!.trigger('click')
    await flushPromises()
    expect(wrapper!.find('pre.preview').exists()).toBe(false)
    expect(wrapper!.text()).toContain('there is no summary')
  })

  // Keyboard focus lands on the select's input, so that is what carries the name.
  it('names each select where focus lands', async () => {
    serve()
    mountPage()
    await flushPromises()

    for (const label of ['Game mode', 'Session mode', 'Grid order', 'AI difficulty']) {
      expect(input(label).exists(), label).toBe(true)
    }
  })
})
