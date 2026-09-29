import { describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { mount } from '@vue/test-utils'
import HomeView from './HomeView.vue'
import type { PublicOverview } from '@/composables/usePublicOverview'

const state = vi.hoisted(() => ({ overview: null as unknown, error: null as string | null }))

vi.mock('@/composables/usePublicOverview', () => ({
  usePublicOverview: () => ({
    overview: ref(state.overview),
    error: ref(state.error),
    loading: ref(false),
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
  // A failed refresh must not leave stale data - "Online" included - looking current.
  it('says the data may be out of date when a refresh failed', () => {
    state.overview = overview
    state.error = "The controller's database is unavailable."

    const text = mount(HomeView).text()

    expect(text).toContain('Test server')
    expect(text).toContain("The controller's database is unavailable.")
    expect(text).toContain('it may be out of date')
  })

  it('shows no warning when the data is current', () => {
    state.overview = overview
    state.error = null

    expect(mount(HomeView).text()).not.toContain('out of date')
  })
})
