<script setup lang="ts">
import { computed, nextTick, ref, toRaw } from 'vue'
import { NButton, NInputNumber, NSelect, NSpace } from 'naive-ui'
import type { components } from '@/api/schema'

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

function remove(index: number) {
  tracks.value = tracks.value.filter((_, i) => i !== index)
}

function update(index: number, patch: Partial<Track>) {
  tracks.value = tracks.value.map((t, i) => (i === index ? { ...t, ...patch } : t))
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

function errorOf(index: number): string | undefined {
  const errors = props.errors ?? {}
  return errors[`tracks[${index}].track`] ?? errors[`tracks[${index}]`]
}
</script>

<template>
  <div>
    <ol ref="list" class="tracks" aria-label="Tracks, in rotation order">
      <li v-for="(track, index) in tracks" :key="keyOf(track)" :data-row="keyOf(track)" class="row">
        <span class="position" aria-hidden="true">{{ index + 1 }}</span>
        <div class="main">
          <strong>{{ nameOf(track) }}</strong>
          <span v-if="errorOf(index)" class="error" role="alert">{{ errorOf(index) }}</span>
          <NSpace size="small" align="center" class="fields">
            <NSelect
              :value="track.gamemode ?? ''"
              :options="modeOptions"
              size="small"
              filterable
              style="width: 170px"
              :disabled="disabled"
              :input-props="{ 'aria-label': `Game mode for ${nameOf(track)}` }"
              @update:value="(value: string) => update(index, { gamemode: value || null })"
            />
            <NInputNumber
              :value="track.laps ?? null"
              size="small"
              :min="1"
              :max="60"
              placeholder="Laps"
              style="width: 110px"
              :disabled="disabled"
              :input-props="{ 'aria-label': `Laps for ${nameOf(track)}` }"
              @update:value="(value: number | null) => update(index, { laps: value })"
            />
            <NInputNumber
              :value="track.bots ?? null"
              size="small"
              :min="0"
              :max="24"
              placeholder="Bots"
              style="width: 110px"
              :disabled="disabled"
              :input-props="{ 'aria-label': `AI bots for ${nameOf(track)}` }"
              @update:value="(value: number | null) => update(index, { bots: value })"
            />
          </NSpace>
        </div>
        <NSpace size="small" class="actions">
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
          <NButton size="small" type="error" ghost :disabled="disabled" :aria-label="`Remove ${nameOf(track)}`" @click="remove(index)">
            Remove
          </NButton>
        </NSpace>
      </li>
    </ol>
    <p v-if="!tracks.length" class="muted">No tracks yet.</p>
    <NSelect
      v-model:value="adding"
      :options="pickOptions"
      filterable
      clearable
      placeholder="Add a track layout..."
      :disabled="disabled"
      :input-props="{ 'aria-label': 'Add a track layout' }"
      @update:value="add"
    />
  </div>
</template>

<style scoped>
.tracks {
  list-style: none;
  padding: 0;
  margin: 0 0 12px;
}
.row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: flex-start;
  padding: 8px 0;
  border-bottom: 1px solid var(--n-border-color, rgba(128, 128, 128, 0.2));
}
.position {
  width: 2ch;
  text-align: right;
  opacity: 0.6;
  padding-top: 2px;
}
.main {
  flex: 1;
  min-width: 0;
}
.fields {
  margin-top: 6px;
}
.error {
  display: block;
  color: #d03050;
}
.actions {
  flex-shrink: 0;
}
</style>
