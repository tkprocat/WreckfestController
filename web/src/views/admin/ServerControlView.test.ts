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
})
