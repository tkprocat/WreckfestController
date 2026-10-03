import { afterEach, describe, expect, it } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import type { components } from '@/api/schema'
import TrackListEditor from './TrackListEditor.vue'

type Track = components['schemas']['EventLoopTrack']
type Variant = components['schemas']['VariantResponse']

const variant = (id: number, variantId: string, trackName: string, name: string, gameMode: 'Racing' | 'Derby' = 'Racing'): Variant => ({
  id,
  variantId,
  name,
  gameMode,
  trackId: id,
  trackKey: variantId,
  trackName,
  allowedForVoting: true,
  isBuiltIn: true,
  isHidden: false,
  version: 1,
  tags: [],
})

const variants = [variant(1, 'loop', 'Fields', 'Loop'), variant(2, 'arena', 'Crash Arena', 'Bowl', 'Derby'), { ...variant(3, 'quarry', 'Old Quarry', 'Pit'), isHidden: true }]

let wrapper: VueWrapper | undefined
afterEach(() => {
  wrapper?.unmount()
  wrapper = undefined
  document.body.innerHTML = ''
})

function editor(initial: Track[]) {
  const tracks = ref<Track[]>(initial)
  wrapper = mount(
    defineComponent({
      render: () => h(TrackListEditor, { modelValue: tracks.value, 'onUpdate:modelValue': (v: Track[]) => (tracks.value = v), variants }),
    }),
    { attachTo: document.body },
  )
  return tracks
}

const button = (label: string) => wrapper!.find(`button[aria-label="${label}"]`)

describe('TrackListEditor', () => {
  it('names rows by their layout, or by their id when the catalogue does not know it', () => {
    editor([{ track: 'loop' }, { track: 'mystery_track' }])

    expect(wrapper!.text()).toContain('Fields - Loop')
    expect(wrapper!.text()).toContain('mystery_track (not in the catalogue)')
  })

  // A layout hidden since it was added stays in the rotation, marked, not dropped.
  it('keeps a hidden layout and duplicate rows, each as its own row', () => {
    editor([{ track: 'quarry' }, { track: 'loop' }, { track: 'loop', laps: 5 }])

    const rows = wrapper!.findAll('li.row')
    expect(rows).toHaveLength(3)
    expect(rows[0]!.text()).toContain('Old Quarry - Pit')
    expect(rows[0]!.text()).toContain('Hidden')
    expect(rows[1]!.text()).not.toContain('Hidden')
    expect(new Set(rows.map((r) => r.attributes('data-row'))).size).toBe(3)
    expect(wrapper!.text()).toContain('3 tracks')
  })

  // Every field survives a move, including the ones the editor does not show.
  it('moves a row down, keeping its fields, and focus follows it', async () => {
    const tracks = editor([
      { track: 'loop', laps: 3, weather: 'rain', numTeams: 2 },
      { track: 'arena', gamemode: 'derby' },
    ])

    await button('Move Fields - Loop down').trigger('click')
    await flushPromises()

    expect(tracks.value).toEqual([{ track: 'arena', gamemode: 'derby' }, { track: 'loop', laps: 3, weather: 'rain', numTeams: 2 }])
    // At the bottom now, so Down is disabled: focus goes to its Up.
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Move Fields - Loop up')
  })

  it('disables Up on the first row and Down on the last', () => {
    editor([{ track: 'loop' }, { track: 'arena' }])

    expect(button('Move Fields - Loop up').attributes('disabled')).toBeDefined()
    expect(button('Move Crash Arena - Bowl down').attributes('disabled')).toBeDefined()
  })

  it('removes a row, and focus moves to the row that took its place', async () => {
    const tracks = editor([{ track: 'loop' }, { track: 'arena' }])

    await button('Remove Fields - Loop').trigger('click')
    await flushPromises()

    expect(tracks.value).toEqual([{ track: 'arena' }])
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Remove Crash Arena - Bowl')
  })

  it('focuses the picker when the last row is removed', async () => {
    editor([{ track: 'loop' }])

    await button('Remove Fields - Loop').trigger('click')
    await flushPromises()

    expect(document.activeElement?.getAttribute('aria-label')).toBe('Add a track layout')
  })

  // A changed field makes a new row object; the row must not remount under the user's cursor.
  it('keeps the row and its focus while a number is typed', async () => {
    const tracks = editor([{ track: 'loop', weather: 'rain' }])
    const laps = () => wrapper!.find<HTMLInputElement>('input[aria-label="Laps for Fields - Loop"]')
    const before = laps().element
    before.focus()

    await laps().setValue('1')
    await laps().setValue('12')
    await flushPromises()

    expect(laps().element).toBe(before)
    expect(document.activeElement).toBe(before)
    expect(tracks.value).toEqual([{ track: 'loop', weather: 'rain', laps: 12 }])
  })

  // The API allows any non-negative count; the editor must not clamp saved values on blur.
  it('leaves values the API allows unchanged when tabbing through them', async () => {
    const tracks = editor([{ track: 'loop', laps: 0, bots: 30 }])

    for (const label of ['Laps for Fields - Loop', 'AI bots for Fields - Loop']) {
      const input = wrapper!.find(`input[aria-label="${label}"]`)
      await input.trigger('focus')
      await input.trigger('blur')
    }
    await flushPromises()

    expect(tracks.value).toEqual([{ track: 'loop', laps: 0, bots: 30 }])
  })

  it('shows the server message on its row', () => {
    const tracks = ref<Track[]>([{ track: 'loop' }, { track: 'bad id' }])
    wrapper = mount(TrackListEditor, {
      props: { modelValue: tracks.value, variants, errors: { 'tracks[1].track': 'must be a game track id' } },
    })

    const rows = wrapper.findAll('li')
    expect(rows[1]!.text()).toContain('must be a game track id')
    expect(rows[0]!.text()).not.toContain('must be')
  })
})
