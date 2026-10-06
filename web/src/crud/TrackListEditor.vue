<script setup lang="ts">
import { computed, nextTick, ref, toRaw } from 'vue'
import { NButton, NInputNumber, NSelect, NTag } from 'naive-ui'
import type { components } from '@/api/schema'
import { vSelectFocus } from './selectFocus'

/**
 * A rotation: tracks in order, each with its game mode, laps and bots. Used by collections
 * and the server rotation. Rows keep every field they came with - the ones not shown here
 * (teams, car rules, weather) are saved as they were.
 *
 * Reordering is by Up/Down buttons (drag and drop may come later); focus follows the row
 * that moved, so a keyboard user can keep pressing.
 */
type Track = components['schemas']['EventLoopTrack']
type Variant = components['schemas']['VariantResponse']

const tracks = defineModel<Track[]>({ required: true })
const props = defineProps<{
  /** The catalogue's layouts, to pick from and to label rows by. */
  variants: Variant[]
  disabled?: boolean
  /** Server messages, keyed as the request named them: `tracks[2].track`. */
  errors?: Record<string, string>
}>()

const GAME_MODES = ['racing', 'derby', 'derby deathmatch', 'team derby', 'team race', 'elimination race']
const modeOptions = [{ label: 'Server default', value: '' }, ...GAME_MODES.map((m) => ({ label: m, value: m }))]

const byId = computed(() => new Map(props.variants.map((v) => [v.variantId.toLowerCase(), v])))
const pickOptions = computed(() =>
  props.variants
    .filter((v) => !v.isHidden)
    .map((v) => ({ label: labelOf(v), value: v.variantId }))
    .sort((a, b) => a.label.localeCompare(b.label)),
)

function labelOf(variant: Variant): string {
  return `${variant.trackName} - ${variant.name}`
}

/** What a row is called: its layout's name, or its raw id when the catalogue does not know it. */
function nameOf(track: Track): string {
  const id = track.track ?? ''
  const variant = byId.value.get(id.toLowerCase())
  return variant ? labelOf(variant) : `${id} (not in the catalogue)`
}

// Stable keys for rows as they move: the row objects themselves move, so key by identity.
const keys = new WeakMap<object, number>()
let nextKey = 0
function keyOf(track: Track): number {
  const raw = toRaw(track)
  let key = keys.get(raw)
  if (key === undefined) {
    key = ++nextKey
    keys.set(raw, key)
  }

  return key
}

const list = ref<HTMLElement | null>(null)
const picker = ref<HTMLElement | null>(null)

async function move(index: number, by: -1 | 1) {
  const target = index + by
  const next = [...tracks.value]
  const [row] = next.splice(index, 1)
  next.splice(target, 0, row!)
  tracks.value = next

  // Keep focus on the moved row; at an end, the button it pressed is now disabled.
  await nextTick()
  const at = target === 0 && by === -1 ? 'down' : target === next.length - 1 && by === 1 ? 'up' : by === -1 ? 'up' : 'down'
  list.value?.querySelector<HTMLButtonElement>(`[data-row="${keyOf(row!)}"] [data-move="${at}"]`)?.focus()
}

async function remove(index: number) {
  const next = tracks.value.filter((_, i) => i !== index)
  tracks.value = next

  // Focus the row that took its place (or the one above), else the picker.
  await nextTick()
  const neighbour = next[Math.min(index, next.length - 1)]
  const target = neighbour
    ? list.value?.querySelector<HTMLButtonElement>(`[data-row="${keyOf(neighbour)}"] [data-remove]`)
    : picker.value?.querySelector<HTMLInputElement>('input')
  target?.focus()
}

/** A changed field makes a new row object; it keeps the old one's key, so the row (and its focus) stays. */
function update(index: number, patch: Partial<Track>) {
  tracks.value = tracks.value.map((t, i) => {
    if (i !== index) {
      return t
    }

    const changed = { ...t, ...patch }
    keys.set(changed, keyOf(t))
    return changed
  })
}

const adding = ref<string | null>(null)
function add(variantId: string | null) {
  if (!variantId) {
    return
  }

  const variant = byId.value.get(variantId.toLowerCase())
  tracks.value = [...tracks.value, { track: variantId, gamemode: variant?.gameMode === 'Derby' ? 'derby' : 'racing' }]
  adding.value = null
}

/** Whether the catalogue knows the row's layout; an unknown id is kept as it is. */
function known(track: Track): boolean {
  return byId.value.has((track.track ?? '').toLowerCase())
}

/** A layout hidden in the catalogue: still in this rotation, never dropped for it. */
function hiddenIn(track: Track): boolean {
  return byId.value.get((track.track ?? '').toLowerCase())?.isHidden === true
}

function errorOf(index: number): string | undefined {
  const errors = props.errors ?? {}
  return errors[`tracks[${index}].track`] ?? errors[`tracks[${index}]`]
}
</script>

<template>
  <div class="track-list">
    <ol ref="list" class="tracks" aria-label="Tracks, in rotation order">
      <li v-for="(track, index) in tracks" :key="keyOf(track)" :data-row="keyOf(track)" class="row" :class="{ invalid: errorOf(index) }">
        <span class="position" aria-hidden="true">{{ index + 1 }}</span>
        <div class="identity">
          <span class="row-name">{{ nameOf(track) }}</span>
          <span v-if="known(track)" class="row-id">{{ track.track }}</span>
          <NTag v-if="hiddenIn(track)" size="small" :bordered="false" class="flag">Hidden</NTag>
          <span v-if="errorOf(index)" class="error" role="alert">{{ errorOf(index) }}</span>
        </div>
        <div class="settings">
          <label class="setting">
            <span class="setting-label" aria-hidden="true">Mode</span>
            <NSelect
              :value="track.gamemode ?? ''"
              :options="modeOptions"
              size="small"
              filterable
              :disabled="disabled"
              v-select-focus="{ 'aria-label': `Game mode for ${nameOf(track)}` }"
              :input-props="{ 'aria-label': `Game mode for ${nameOf(track)}` }"
              @update:value="(value: string) => update(index, { gamemode: value || null })"
            />
          </label>
          <label class="setting">
            <span class="setting-label" aria-hidden="true">Laps</span>
            <NInputNumber
              :value="track.laps ?? null"
              size="small"
              :min="0"
              placeholder="Default"
              :show-button="false"
              :disabled="disabled"
              :input-props="{ 'aria-label': `Laps for ${nameOf(track)}` }"
              @update:value="(value: number | null) => update(index, { laps: value })"
            />
          </label>
          <label class="setting">
            <span class="setting-label" aria-hidden="true">Bots</span>
            <NInputNumber
              :value="track.bots ?? null"
              size="small"
              :min="0"
              placeholder="Default"
              :show-button="false"
              :disabled="disabled"
              :input-props="{ 'aria-label': `AI bots for ${nameOf(track)}` }"
              @update:value="(value: number | null) => update(index, { bots: value })"
            />
          </label>
        </div>
        <div class="actions" role="group" :aria-label="`Order of ${nameOf(track)}`">
          <NButton
            size="small"
            data-move="up"
            :disabled="disabled || index === 0"
            :aria-label="`Move ${nameOf(track)} up`"
            @click="move(index, -1)"
          >
            Up
          </NButton>
          <NButton
            size="small"
            data-move="down"
            :disabled="disabled || index === tracks.length - 1"
            :aria-label="`Move ${nameOf(track)} down`"
            @click="move(index, 1)"
          >
            Down
          </NButton>
          <NButton size="small" type="error" quaternary data-remove :disabled="disabled" :aria-label="`Remove ${nameOf(track)}`" @click="remove(index)">
            Remove
          </NButton>
        </div>
      </li>
    </ol>
    <p v-if="!tracks.length" class="empty muted">No tracks yet. Add a layout below.</p>
    <div ref="picker" class="picker">
      <NSelect
        v-model:value="adding"
        :options="pickOptions"
        filterable
        clearable
        placeholder="Add a track layout..."
        :disabled="disabled"
        v-select-focus="{ 'aria-label': 'Add a track layout' }"
        :input-props="{ 'aria-label': 'Add a track layout' }"
        @update:value="add"
      />
      <span v-if="tracks.length" class="count muted">{{ tracks.length }} {{ tracks.length === 1 ? 'track' : 'tracks' }}</span>
    </div>
  </div>
</template>

<style scoped>
/* Sized by where it sits (a page card or a dialog), not by the screen. */
.track-list { container-type: inline-size; }
.tracks {
  list-style: none;
  padding: 0;
  margin: 0 0 12px;
  border: 1px solid var(--border-color);
  border-radius: var(--radius-control);
}
.tracks:empty { display: none; }
/* One row: position, what it is, its settings, its order controls. */
.row {
  display: grid;
  grid-template-columns: 32px minmax(180px, 1fr) minmax(0, 340px) auto;
  gap: 12px;
  align-items: center;
  padding: 10px 12px;
}
.row + .row { border-top: 1px solid var(--border-color); }
.row.invalid { box-shadow: inset 3px 0 0 var(--error-color); }
.position {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 50%;
  background: var(--border-color);
  font-size: var(--font-meta);
  font-weight: 700;
  font-variant-numeric: tabular-nums;
}
.identity {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  min-width: 0;
}
.flag { margin-top: 2px; }
.settings {
  display: grid;
  grid-template-columns: minmax(0, 1.6fr) minmax(0, 1fr) minmax(0, 1fr);
  gap: 8px;
}
.setting {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.setting-label {
  color: var(--text-muted);
  font-size: var(--font-meta);
}
.actions {
  display: flex;
  gap: 4px;
  justify-content: flex-end;
}
.error {
  color: var(--error-color);
  font-size: var(--font-meta);
}
.empty { margin: 0 0 12px; }
.picker {
  display: flex;
  align-items: center;
  gap: 12px;
}
.picker > :first-child { flex: 1 1 auto; min-width: 0; max-width: 480px; }
.count { margin-left: auto; font-size: var(--font-meta); white-space: nowrap; }
/* Narrower: settings under the name, controls under them. */
@container (max-width: 760px) {
  .row { grid-template-columns: 32px minmax(0, 1fr); align-items: start; }
  .settings, .actions { grid-column: 2; }
  .actions { justify-content: flex-start; flex-wrap: wrap; }
}
@container (max-width: 380px) {
  .settings { grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); }
  .settings > :first-child { grid-column: 1 / -1; }
}
</style>
