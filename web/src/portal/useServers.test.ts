import { afterEach, describe, expect, it, vi } from 'vitest'
import { effectScope } from 'vue'
import { REFRESH_MS, useServers, type PortalPage } from './useServers'

const page: PortalPage = { title: 'Monday Night Wrecking', intro: null, servers: [] }
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

afterEach(() => vi.useRealTimers())

function start(fetcher: typeof fetch) {
  const scope = effectScope()
  const servers = scope.run(() => useServers(fetcher))!
  return { ...servers, stop: () => scope.stop() }
}

describe('useServers', () => {
  it('loads the page', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValue(json(page))
    const servers = start(fetcher)

    await vi.waitFor(() => expect(servers.loading.value).toBe(false))

    expect(fetcher).toHaveBeenCalledWith('/api/servers', expect.anything())
    expect(servers.page.value?.title).toBe('Monday Night Wrecking')
    expect(servers.stale.value).toBe(false)
    servers.stop()
  })

  it('keeps the last page when a refresh fails, and says so', async () => {
    const fetcher = vi.fn<typeof fetch>().mockResolvedValueOnce(json(page)).mockResolvedValueOnce(json({}, 502))
    const servers = start(fetcher)
    await vi.waitFor(() => expect(servers.page.value).not.toBeNull())

    await servers.reload()

    expect(servers.page.value?.title).toBe('Monday Night Wrecking')
    expect(servers.stale.value).toBe(true)
    servers.stop()
  })

  it('refreshes on a timer until the scope ends', async () => {
    vi.useFakeTimers()
    const fetcher = vi.fn<typeof fetch>().mockImplementation(() => Promise.resolve(json(page)))
    const servers = start(fetcher)

    await vi.advanceTimersByTimeAsync(REFRESH_MS)
    expect(fetcher).toHaveBeenCalledTimes(2)

    servers.stop()
    await vi.advanceTimersByTimeAsync(REFRESH_MS * 2)
    expect(fetcher).toHaveBeenCalledTimes(2)
  })
})
