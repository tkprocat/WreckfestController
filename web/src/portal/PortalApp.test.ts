import { beforeEach, describe, expect, it, vi } from 'vitest'
import { ref } from 'vue'
import { mount } from '@vue/test-utils'
import StatusBadge from '@/components/StatusBadge.vue'
import PortalApp from './PortalApp.vue'
import type { PortalPage, ServerCard } from './useServers'

const state = vi.hoisted(() => ({ page: null as unknown, stale: false, loading: false }))

vi.mock('./useServers', () => ({
  useServers: () => ({ page: ref(state.page), stale: ref(state.stale), loading: ref(state.loading), reload: vi.fn() }),
}))

const card = (overrides: Partial<ServerCard>): ServerCard => ({
  id: 'tvtp2',
  name: 'TVTP2',
  description: 'Tracks from the TVTP2 mod.',
  link: '/tvtp2/',
  state: 'online',
  serverName: '^5Monday ^1Night',
  humans: 3,
  bots: 1,
  maxPlayers: 24,
  currentTrack: 'Big Valley - Main Route',
  checkedAt: '2026-10-14T18:00:00Z',
  lastSeenAt: '2026-10-14T18:00:00Z',
  ...overrides,
})

const page = (...servers: ServerCard[]): PortalPage => ({ title: 'Monday Night Wrecking', intro: 'Pick a server.', servers })

beforeEach(() => {
  state.page = null
  state.stale = false
  state.loading = false
})

describe('PortalApp', () => {
  it('shows each server with its status, players and track, linked to its site', () => {
    state.page = page(card({}), card({ id: 'vanilla', name: 'Vanilla', link: '/vanilla/', state: 'server-stopped', humans: 0, bots: 0 }))

    const wrapper = mount(PortalApp)
    const servers = wrapper.findAll('.server')

    expect(wrapper.find('h1').text()).toBe('Monday Night Wrecking')
    expect(servers).toHaveLength(2)
    expect(servers[0]!.find('h2 a').attributes('href')).toBe('/tvtp2/')
    expect(servers[0]!.findComponent(StatusBadge).text()).toBe('UP')
    expect(servers[0]!.text()).toContain('3 players, 1 bot / 24 total')
    expect(servers[0]!.text()).toContain('Big Valley - Main Route')
    expect(servers[0]!.find('.server-name').text()).toBe('Monday Night')
    expect(servers[1]!.findComponent(StatusBadge).text()).toBe('DOWN')
    expect(servers[1]!.text()).toContain('Server offline')
  })

  it('marks an unreachable server\'s details as last known', () => {
    state.page = page(card({ state: 'unreachable' }))

    const wrapper = mount(PortalApp)

    expect(wrapper.findComponent(StatusBadge).text()).toBe('NO CONNECTION')
    expect(wrapper.find('.stale-label').text()).toContain('Last known')
  })

  it('says so when a server never answered', () => {
    state.page = page(card({ state: 'unreachable', lastSeenAt: null, serverName: null, humans: null, bots: null }))

    const wrapper = mount(PortalApp)

    expect(wrapper.text()).toContain('not answering')
    expect(wrapper.find('.facts').exists()).toBe(false)
  })

  it('says the list is not updating when a refresh failed', () => {
    state.page = page(card({}))
    state.stale = true

    const wrapper = mount(PortalApp)

    expect(wrapper.text()).toContain('NOT UPDATING')
    expect(wrapper.text()).toContain('last known status')
  })
})
