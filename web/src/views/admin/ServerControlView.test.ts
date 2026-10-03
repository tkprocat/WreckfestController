import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { NDialogProvider, NMessageProvider } from 'naive-ui'
import ServerControlView from './ServerControlView.vue'

const api = vi.hoisted(() => ({ GET: vi.fn(), POST: vi.fn() }))
const hub = vi.hoisted(() => new Map<string, (message: unknown) => void>())

vi.mock('@/api/client', () => ({ api }))
vi.mock('@/realtime/hub', () => ({
  onHub: (event: string, handler: (message: unknown) => void) => {
    hub.set(event, handler)
    return () => hub.delete(event)
  },
}))

function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((res) => (resolve = res))
  return { promise, resolve }
}

const answer = (data: unknown) => ({ data, error: undefined, response: new Response(null, { status: 200 }) })
const running = answer({ isRunning: true, processId: 42, uptimeSeconds: 60, currentTrack: null })
const stopped = answer({ isRunning: false, processId: null, uptimeSeconds: null, currentTrack: null })
const tail = (...output: string[]) => answer({ lines: output.length, source: 'file', logFilePath: null, output })

/** GET answers by path; each may be a value or a promise the test settles. */
function serve(routes: Record<string, unknown>) {
  api.GET.mockImplementation((path: string) => Promise.resolve(routes[path]))
}

function mountPage(): VueWrapper {
  return mount(
    defineComponent({
      render: () => h(NMessageProvider, () => h(NDialogProvider, () => h(ServerControlView))),
    }),
    { attachTo: document.body },
  )
}

const button = (wrapper: VueWrapper, label: string) =>
  wrapper.findAll('button').find((b) => b.text().trim() === label)!

let wrapper: VueWrapper | undefined

beforeEach(() => {
  api.GET.mockReset()
  api.POST.mockReset()
  hub.clear()
})

afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

describe('ServerControlView', () => {
  // Update stops a running server without asking: it must wait for a known Stopped.
  it('offers no action while the state is unknown', async () => {
    const status = deferred<unknown>()
    serve({ '/api/server/status': status.promise, '/api/server/logfile': tail() })

    wrapper = mountPage()
    await flushPromises()

    for (const label of ['Start', 'Update', 'Stop', 'Restart', 'Force stop']) {
      expect(button(wrapper, label).attributes('disabled'), label).toBeDefined()
    }

    status.resolve(stopped)
    await flushPromises()
    expect(button(wrapper, 'Update').attributes('disabled')).toBeUndefined()
  })

  // A hub event says something changed: until the new status is in, the old Stopped must
  // not offer Update to a server that may have just started.
  it('offers no action while a refresh is on its way', async () => {
    const refresh = deferred<unknown>()
    api.GET.mockImplementation((path: string) =>
      Promise.resolve(path === '/api/server/logfile' ? tail() : stopped),
    )
    wrapper = mountPage()
    await flushPromises()
    expect(button(wrapper, 'Update').attributes('disabled')).toBeUndefined()

    api.GET.mockImplementation((path: string) => (path === '/api/server/logfile' ? Promise.resolve(tail()) : refresh.promise))
    hub.get('ServerStarted')!({})
    await flushPromises()
    expect(button(wrapper, 'Update').attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('Stopped')

    refresh.resolve(running)
    await flushPromises()
    expect(button(wrapper, 'Update').attributes('disabled')).toBeDefined()
    expect(button(wrapper, 'Stop').attributes('disabled')).toBeUndefined()
  })

  it('sends a command once, however often Enter is pressed', async () => {
    serve({ '/api/server/status': running, '/api/server/logfile': tail() })
    const sent = deferred<unknown>()
    api.POST.mockReturnValue(sent.promise)
    wrapper = mountPage()
    await flushPromises()

    const input = wrapper.find('input')
    await input.setValue('/message Hello')
    await input.trigger('keyup', { key: 'Enter' })
    await input.trigger('keyup', { key: 'Enter' })
    sent.resolve(answer({ message: 'Sent' }))
    await flushPromises()

    expect(api.POST).toHaveBeenCalledTimes(1)
  })

  // Like the Stop and Restart buttons: a command that disconnects players asks first.
  // The game's /bot command, as the Laravel page sent it; the typed command stays.
  it('adds an AI bot with the /bot command', async () => {
    serve({ '/api/server/status': running, '/api/server/logfile': tail() })
    api.POST.mockResolvedValue(answer({ message: 'Command sent.' }))
    wrapper = mountPage()
    await flushPromises()
    await wrapper.find('input[aria-label="Console command"]').setValue('/message hi')

    await button(wrapper, 'Add AI bot').trigger('click')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledWith('/api/server/command', { body: { command: '/bot' } })
    expect(document.body.textContent).toContain('Sent /bot: an AI bot joins if there is room.')
    expect((wrapper.find('input[aria-label="Console command"]').element as HTMLInputElement).value).toBe('/message hi')
  })

  // One request at a time: while /bot is on its way, nothing else can be sent.
  it('sends /bot once while it is pending, and frees the controls after a failure', async () => {
    serve({ '/api/server/status': running, '/api/server/logfile': tail() })
    const pending = deferred<unknown>()
    api.POST.mockReturnValueOnce(pending.promise)
    wrapper = mountPage()
    await flushPromises()
    await wrapper.find('input[aria-label="Console command"]').setValue('/message hi')

    await button(wrapper, 'Add AI bot').trigger('click')
    await button(wrapper, 'Add AI bot').trigger('click')
    await wrapper.find('input[aria-label="Console command"]').trigger('keyup.enter')
    await flushPromises()

    expect(api.POST).toHaveBeenCalledTimes(1)
    for (const label of ['Stop', 'Restart', 'Send']) {
      expect(button(wrapper, label).attributes('disabled'), label).toBeDefined()
    }

    pending.resolve({ data: undefined, error: { title: 'The hook is not injected.', status: 409 }, response: new Response(null, { status: 409 }) })
    await flushPromises()

    expect(document.body.textContent).toContain('The hook is not injected.')
    expect(button(wrapper, 'Add AI bot').attributes('disabled')).toBeUndefined()
    expect((wrapper.find('input[aria-label="Console command"]').element as HTMLInputElement).value).toBe('/message hi')
  })

  it('offers Add AI bot only while the server is known to run', async () => {
    const refresh = deferred<unknown>()
    api.GET.mockImplementation((path: string) => (path === '/api/server/status' ? refresh.promise : Promise.resolve(tail())))
    wrapper = mountPage()
    await flushPromises()

    expect(button(wrapper, 'Add AI bot').attributes('disabled')).toBeDefined()
    refresh.resolve(running)
    await flushPromises()
    expect(button(wrapper, 'Add AI bot').attributes('disabled')).toBeUndefined()
  })

  it('offers Add AI bot only while the server runs', async () => {
    serve({ '/api/server/status': stopped, '/api/server/logfile': tail() })
    wrapper = mountPage()
    await flushPromises()

    expect(button(wrapper, 'Add AI bot').attributes('disabled')).toBeDefined()
  })

  it('asks before sending a command that disconnects players', async () => {
    serve({ '/api/server/status': running, '/api/server/logfile': tail() })
    api.POST.mockResolvedValue(answer({ message: 'Sent' }))
    wrapper = mountPage()
    await flushPromises()

    await wrapper.find('input').setValue('exit')
    await button(wrapper, 'Send').trigger('click')
    await flushPromises()

    expect(api.POST).not.toHaveBeenCalled()
    expect(document.body.textContent).toContain('disconnects players')
  })

  it('keeps hub lines that arrive while the log tail is loading', async () => {
    const log = deferred<unknown>()
    serve({ '/api/server/status': running, '/api/server/logfile': log.promise })
    wrapper = mountPage()
    await flushPromises()

    hub.get('ConsoleLog')!({ logs: ['live line'] })
    log.resolve(tail('from the file'))
    await flushPromises()

    expect(wrapper.find('pre').text()).toBe('from the file\nlive line')
  })

  // No answer is not a failure: whether the server started is unknown, and the page says so.
  it('says when an action got no answer', async () => {
    serve({ '/api/server/status': stopped, '/api/server/logfile': tail() })
    api.POST.mockRejectedValue(new TypeError('Failed to fetch'))
    wrapper = mountPage()
    await flushPromises()

    await button(wrapper, 'Start').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('it is not known whether this happened')
  })
  it('keeps recovery actions distinct and confirms a force stop', async () => {
    serve({ '/api/server/status': running, '/api/server/logfile': tail() })
    wrapper = mountPage()
    await flushPromises()
    expect(wrapper.text()).toContain('Recovery')
    await button(wrapper, 'Force stop').trigger('click')
    await flushPromises()
    expect(api.POST).not.toHaveBeenCalled()
    expect(document.body.textContent).toContain('Kill the server process?')
  })

  it('labels a pending console and keeps its scroll region keyboard accessible', async () => {
    const log = deferred<unknown>()
    serve({ '/api/server/status': running, '/api/server/logfile': log.promise })
    wrapper = mountPage()
    await flushPromises()
    expect(wrapper.find('pre').attributes('aria-label')).toBe('Server console')
    expect(wrapper.find('pre').attributes('tabindex')).toBe('0')
    expect(wrapper.find('pre').text()).toContain('Loading console')
    log.resolve(tail('first log line'))
    await flushPromises()
    expect(wrapper.find('pre').text()).toContain('first log line')
  })

})
