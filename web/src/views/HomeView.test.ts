import { describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { mount } from '@vue/test-utils'
import HomeView from './HomeView.vue'
import type { PublicOverview } from '@/composables/usePublicOverview'

const state = vi.hoisted(() => ({ overview: null as unknown, error: null as string | null, loading: false }))

vi.mock('@/composables/usePublicOverview', () => ({
  usePublicOverview: () => ({
    overview: ref(state.overview),
    error: ref(state.error),
    loading: ref(state.loading),
    reload: vi.fn(),
  }),
}))

const overview: PublicOverview = {
  serverName: 'Test server',
  maxPlayers: 24,
  status: { isRunning: true, uptimeSeconds: 60 },
  currentTrack: { name: 'Big Valley' },
  players: { humans: 0, bots: 0, list: [] },
  rotation: { name: null, tracks: [] },
  activeCup: null,
  upcomingCups: [],
  updatedAt: '2026-09-29T06:00:00Z',
} as unknown as PublicOverview

describe('HomeView', () => {
  it('shows a clear loading state before the first snapshot', () => {
    state.overview = null
    state.error = null
    state.loading = true
    expect(mount(HomeView).text()).toContain('Loading server overview')
    state.loading = false
  })

  it('shows an offline state without claiming a stale track is racing or inventing capacity', () => {
    state.overview = {
      ...overview,
      maxPlayers: null,
      status: { isRunning: false, uptimeSeconds: null },
      players: { humans: 0, bots: 1, list: [{ name: 'Practice AI', isBot: true }] },
    }
    state.error = null
    state.loading = false
    const text = mount(HomeView).text()
    expect(text).toContain('Server offline')
    expect(text).toContain('Practice AI')
    expect(text).toContain('Bot')
    expect(text).not.toContain('Racing now')
    expect(text).not.toContain(' / 24')
  })

  it('offers a retry when there is no overview', () => {
    state.overview = null
    state.error = 'The controller cannot be reached.'
    state.loading = false
    const text = mount(HomeView).text()
    expect(text).toContain('Overview unavailable')
    expect(text).toContain('Try again')
  })

  // A failed refresh must not leave stale data - "Online" included - looking current.
  it('says the data may be out of date when a refresh failed', () => {
    state.overview = overview
    state.error = "The controller's database is unavailable."

    const text = mount(HomeView).text()

    expect(text).toContain('Test server')
    expect(text).toContain("The controller's database is unavailable.")
    expect(text).toContain('it may be out of date')
    expect(text).toContain('Last known track')
    expect(text).not.toContain('Racing now')
  })

  it('labels the player counts and uptime as last known when a refresh failed', () => {
    state.overview = { ...overview, players: { humans: 2, bots: 1, list: [] } }
    state.error = 'The controller cannot be reached.'

    const text = mount(HomeView).text()

    expect(text).toContain('human players at last check')
    expect(text).toContain('1 bot at last check')
    expect(text).toContain('Was up 1m at last check')
    expect(text).not.toContain('on the server')
  })

  // The overview can still name an active cup while the server is stopped.
  it('shows the active cup while the server is offline', () => {
    state.overview = { ...overview, status: { isRunning: false, uptimeSeconds: null }, activeCup: { name: 'Sunday Cup' } }
    state.error = null

    expect(mount(HomeView).text()).toContain('Active cup · Sunday Cup')
  })

  it('shows no warning when the data is current', () => {
    state.overview = overview
    state.error = null

    expect(mount(HomeView).text()).not.toContain('out of date')
  })
})
