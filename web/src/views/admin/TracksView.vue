<script setup lang="ts">
import { computed, h, onMounted, ref } from 'vue'
import {
  NButton,
  NCard,
  NCheckbox,
  NDataTable,
  NSelect,
  NSpace,
  NSwitch,
  NTag,
  useMessage,
  type DataTableColumns,
} from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ResourceTable from '@/crud/ResourceTable.vue'
import { NO_ANSWER, hasId, send, useConfirm, type Outcome } from '@/crud/outcome'
import { useResourceList } from '@/crud/useResourceList'
import { textOn } from '@/utils/color'
import TrackEditor from './tracks/TrackEditor.vue'
import VariantEditor from './tracks/VariantEditor.vue'

type Track = components['schemas']['TrackResponse']
type Variant = components['schemas']['VariantResponse']
type Origin = components['schemas']['TrackOrigin']
type Mode = components['schemas']['GameMode']
/** A track as the table shows it: `find` holds its variants' names and ids, for search. */
type Row = Track & { find: string }

const message = useMessage()
const confirm = useConfirm()

// Hidden ones too: the page is where they are unhidden.
const list = useResourceList<Track>(() => api.GET('/api/catalogue/tracks', { params: { query: { includeHidden: true } } }), 'tracks')
const { items: tracks, loading, loaded, error } = list

const trackEditor = ref<InstanceType<typeof TrackEditor> | null>(null)
const variantEditor = ref<InstanceType<typeof VariantEditor> | null>(null)

const ORIGINS: Record<Origin, string> = { BaseGame: 'Base game', Dlc: 'DLC', Workshop: 'Workshop', Custom: 'Custom' }

// Filters, beside the table's own search.
const origin = ref<Origin | null>(null)
const mode = ref<Mode | null>(null)
const tag = ref<string | null>(null)
const weather = ref<string | null>(null)
const showHidden = ref(false)

const tagOptions = computed(() => {
  const tags = new Map<string, string>()
  for (const t of tracks.value) for (const v of t.variants) for (const g of v.tags) tags.set(g.slug, g.name)
  return [...tags].map(([value, label]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label))
})
const weatherOptions = computed(() =>
  [...new Set(tracks.value.flatMap((t) => t.weather))].sort().map((w) => ({ value: w, label: w })),
)

/** A track's variants as the filters leave them: hidden ones only when asked for. */
function variantsOf(track: Track): Variant[] {
  return track.variants.filter(
    (v) => (showHidden.value || !v.isHidden) && (!mode.value || v.gameMode === mode.value) && (!tag.value || v.tags.some((g) => g.slug === tag.value)),
  )
}

const rows = computed<Row[]>(() =>
  tracks.value
    .filter(
      (t) =>
        (showHidden.value || !t.isHidden) &&
        (!origin.value || t.origin === origin.value) &&
        (!weather.value || t.weather.includes(weather.value)) &&
        (!(mode.value || tag.value) || variantsOf(t).length > 0),
    )
    .map((t) => ({ ...t, find: variantsOf(t).map((v) => `${v.name} ${v.variantId}`).join(' ') })),
)

/** One action at a time, on a track (`t1`) or a variant (`v1`). */
const busy = ref<string | null>(null)

async function run(key: string, action: () => Promise<void>) {
  if (busy.value !== null) {
    return
  }

  busy.value = key
  try {
    await action()
  } finally {
    busy.value = null
  }
}

function fail(outcome: Outcome<unknown>, fallback: string) {
  message.error(outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : fallback)
}

/** A 409 with the row: someone else saved it at the same moment, and theirs won. */
const CHANGED_ELSEWHERE = 'Someone else changed this at the same moment, so nothing was changed. It now shows as saved; try again if still needed.'

const isTrack = (body: unknown): body is Track => hasId(body) && 'variants' in body
const isVariant = (body: unknown): body is Variant => hasId(body) && 'variantId' in body

/** A variant the server answered with, put into its track: changed in place, or added. */
function replaceVariant(variant: Variant) {
  const track = tracks.value.find((t) => t.id === variant.trackId)
  if (track) {
    const known = track.variants.some((v) => v.id === variant.id)
    const variants = known ? track.variants.map((v) => (v.id === variant.id ? variant : v)) : [...track.variants, variant]
    list.replace({ ...track, variants: variants.sort((a, b) => a.name.localeCompare(b.name)) })
  }
}

async function trackAction(track: Track, action: 'hide' | 'unhide' | 'reset', done: string) {
  await run(`t${track.id}`, async () => {
    const path = { params: { path: { id: track.id } } }
    const request =
      action === 'hide'
        ? () => api.POST('/api/catalogue/tracks/{id}/hide', path)
        : action === 'unhide'
          ? () => api.POST('/api/catalogue/tracks/{id}/unhide', path)
          : () => api.POST('/api/catalogue/tracks/{id}/reset', path)
    const outcome = await send(request, isTrack, `The track was not changed.`)
    if (outcome.kind === 'ok' && isTrack(outcome.row)) {
      list.replace(outcome.row)
      message.success(done)
    } else if (outcome.kind === 'conflict') {
      list.replace(outcome.current)
      message.warning(CHANGED_ELSEWHERE)
    } else {
      fail(outcome, 'The track was not changed.')
    }
  })
}

async function resetTrack(track: Track) {
  if (busy.value !== null) return
  const sure = await confirm({
    title: 'Reset track',
    content: `Put "${track.name}" back as it shipped: its name, origin and weather? Its variants are left alone.`,
    positive: 'Reset',
  })
  if (sure) await trackAction(track, 'reset', `"${track.name}" reset.`)
}

async function deleteTrack(track: Track) {
  if (busy.value !== null) return
  const sure = await confirm({
    title: 'Delete track',
    content: `Delete "${track.name}" and its ${track.variants.length} variants? This cannot be undone.`,
    positive: 'Delete',
  })
  if (!sure) return

  await run(`t${track.id}`, async () => {
    const outcome = await send(() => api.DELETE('/api/catalogue/tracks/{id}', { params: { path: { id: track.id } } }), isTrack, 'The track was not deleted.')
    if (outcome.kind === 'ok') {
      list.remove(track.id)
      message.success(`"${track.name}" deleted.`)
    } else {
      fail(outcome, 'The track was not deleted.')
    }
  })
}

async function variantAction(variant: Variant, action: 'hide' | 'unhide' | 'reset' | 'vote' | 'novote', done: string) {
  await run(`v${variant.id}`, async () => {
    const path = { params: { path: { id: variant.id } } }
    const request =
      action === 'hide'
        ? () => api.POST('/api/catalogue/variants/{id}/hide', path)
        : action === 'unhide'
          ? () => api.POST('/api/catalogue/variants/{id}/unhide', path)
          : action === 'reset'
            ? () => api.POST('/api/catalogue/variants/{id}/reset', path)
            : () => api.PUT('/api/catalogue/variants/{id}/voting', { ...path, body: { allowed: action === 'vote' } })
    const outcome = await send(request, isVariant, 'The variant was not changed.')
    if (outcome.kind === 'ok' && isVariant(outcome.row)) {
      replaceVariant(outcome.row)
      message.success(done)
    } else if (outcome.kind === 'conflict') {
      replaceVariant(outcome.current)
      message.warning(CHANGED_ELSEWHERE)
    } else {
      fail(outcome, 'The variant was not changed.')
    }
  })
}

async function resetVariant(variant: Variant) {
  if (busy.value !== null) return
  const sure = await confirm({
    title: 'Reset variant',
    content: `Put "${variant.name}" back as it shipped: its name, game mode, voting and tags?`,
    positive: 'Reset',
  })
  if (sure) await variantAction(variant, 'reset', `"${variant.name}" reset.`)
}

async function deleteVariant(variant: Variant) {
  if (busy.value !== null) return
  const sure = await confirm({
    title: 'Delete variant',
    content: `Delete "${variant.name}" (${variant.variantId})? This cannot be undone.`,
    positive: 'Delete',
  })
  if (!sure) return

  await run(`v${variant.id}`, async () => {
    const outcome = await send(() => api.DELETE('/api/catalogue/variants/{id}', { params: { path: { id: variant.id } } }), isVariant, 'The variant was not deleted.')
    const track = tracks.value.find((t) => t.id === variant.trackId)
    if (outcome.kind === 'ok') {
      if (track) list.replace({ ...track, variants: track.variants.filter((v) => v.id !== variant.id) })
      message.success(`"${variant.name}" deleted.`)
    } else {
      fail(outcome, 'The variant was not deleted.')
    }
  })
}

const disabled = () => busy.value !== null

const variantColumns = (track: Track): DataTableColumns<Variant> => [
  {
    title: 'Variant',
    key: 'name',
    render: (v) => h('span', [v.name, ' ', h('code', { class: 'muted' }, v.variantId), v.isHidden ? h(NTag, { size: 'small', class: 'flag' }, () => 'Hidden') : null]),
  },
  { title: 'Mode', key: 'gameMode', width: 90 },
  {
    title: 'Tags',
    key: 'tags',
    render: (v) =>
      h(NSpace, { size: 4 }, () =>
        v.tags.map((g) =>
          h(NTag, { size: 'small', key: g.slug, color: g.color ? { color: g.color, textColor: textOn(g.color), borderColor: g.color } : undefined }, () => g.name),
        ),
      ),
  },
  {
    title: 'Votable',
    key: 'allowedForVoting',
    width: 90,
    render: (v) =>
      h(NSwitch, {
        value: v.allowedForVoting,
        size: 'small',
        loading: busy.value === `v${v.id}`,
        disabled: disabled(),
        'aria-label': `Players can vote for ${track.name} - ${v.name}`,
        'onUpdate:value': (on: boolean) =>
          void variantAction(v, on ? 'vote' : 'novote', on ? `Players can vote for "${v.name}".` : `"${v.name}" is no longer votable.`),
      }),
  },
  {
    title: 'Actions',
    key: 'actions',
    width: 260,
    render: (v) =>
      h(NSpace, { size: 'small' }, () => [
        h(NButton, { size: 'small', disabled: disabled(), 'aria-label': `Edit ${v.name}`, onClick: () => void variantEditor.value?.start(track, v) }, () => 'Edit'),
        v.isHidden
          ? h(NButton, { size: 'small', disabled: disabled(), 'aria-label': `Unhide ${v.name}`, onClick: () => void variantAction(v, 'unhide', `"${v.name}" is shown again.`) }, () => 'Unhide')
          : h(NButton, { size: 'small', disabled: disabled(), 'aria-label': `Hide ${v.name}`, onClick: () => void variantAction(v, 'hide', `"${v.name}" hidden.`) }, () => 'Hide'),
        v.isBuiltIn
          ? h(NButton, { size: 'small', disabled: disabled(), 'aria-label': `Reset ${v.name}`, onClick: () => void resetVariant(v) }, () => 'Reset')
          : h(NButton, { size: 'small', type: 'error', ghost: true, disabled: disabled(), 'aria-label': `Delete ${v.name}`, onClick: () => void deleteVariant(v) }, () => 'Delete'),
      ]),
  },
]

const columns: DataTableColumns<Row> = [
  {
    type: 'expand',
    renderExpand: (t) =>
      h(NDataTable, { columns: variantColumns(t), data: variantsOf(t), rowKey: (v: Variant) => v.id, size: 'small', bordered: false }),
  },
  {
    title: 'Track',
    key: 'name',
    sorter: (a, b) => a.name.localeCompare(b.name),
    render: (t) => h('span', [t.name, ' ', h('code', { class: 'muted' }, t.key), t.isHidden ? h(NTag, { size: 'small', class: 'flag' }, () => 'Hidden') : null]),
  },
  { title: 'Origin', key: 'origin', width: 120, render: (t) => (t.origin === 'Dlc' && t.dlcName ? `DLC: ${t.dlcName}` : t.mod ? `Workshop: ${t.mod.name}` : ORIGINS[t.origin]) },
  { title: 'Variants', key: 'variants', width: 90, render: (t) => String(variantsOf(t).length) },
  { title: 'Weather', key: 'weather', render: (t) => t.weather.join(', ') },
  {
    title: 'Actions',
    key: 'actions',
    width: 360,
    render: (t) =>
      h(NSpace, { size: 'small' }, () => [
        h(NButton, { size: 'small', disabled: disabled(), 'aria-label': `Edit ${t.name}`, onClick: () => void trackEditor.value?.start(t) }, () => 'Edit'),
        h(NButton, { size: 'small', disabled: disabled(), 'aria-label': `Add a variant to ${t.name}`, onClick: () => void variantEditor.value?.start(t, null) }, () => 'Add variant'),
        t.isHidden
          ? h(NButton, { size: 'small', loading: busy.value === `t${t.id}`, disabled: disabled(), 'aria-label': `Unhide ${t.name}`, onClick: () => void trackAction(t, 'unhide', `"${t.name}" is shown again.`) }, () => 'Unhide')
          : h(NButton, { size: 'small', loading: busy.value === `t${t.id}`, disabled: disabled(), 'aria-label': `Hide ${t.name}`, onClick: () => void trackAction(t, 'hide', `"${t.name}" hidden.`) }, () => 'Hide'),
        t.isBuiltIn
          ? h(NButton, { size: 'small', disabled: disabled(), 'aria-label': `Reset ${t.name}`, onClick: () => void resetTrack(t) }, () => 'Reset')
          : h(NButton, { size: 'small', type: 'error', ghost: true, disabled: disabled(), 'aria-label': `Delete ${t.name}`, onClick: () => void deleteTrack(t) }, () => 'Delete'),
      ]),
  },
]

/**
 * The table's expand trigger is a click-only div: a button inside it takes focus and
 * Enter/Space, and its click reaches the trigger.
 */
const expandIcon = ({ expanded, rowData }: { expanded: boolean; rowData: object }) =>
  h('button', { type: 'button', class: 'expander', 'aria-expanded': String(expanded), 'aria-label': `Variants of ${(rowData as Track).name}` }, '›')

onMounted(() => void list.reload())
</script>

<template>
  <section>
    <h1>Tracks</h1>
    <NCard title="Tracks">
      <p class="muted">
        Every track the server knows, with its variants: the ids the game loads. Hidden ones stay out of votes and pickers.
      </p>
      <NSpace class="filters" align="center">
        <NSelect
          v-model:value="origin"
          :options="Object.entries(ORIGINS).map(([value, label]) => ({ value, label }))"
          clearable
          filterable
          placeholder="Any origin"
          style="width: 150px"
          :input-props="{ 'aria-label': 'Origin' }"
        />
        <NSelect
          v-model:value="mode"
          :options="[{ value: 'Racing', label: 'Racing' }, { value: 'Derby', label: 'Derby' }]"
          clearable
          filterable
          placeholder="Any mode"
          style="width: 130px"
          :input-props="{ 'aria-label': 'Game mode' }"
        />
        <NSelect
          v-model:value="tag"
          :options="tagOptions"
          clearable
          filterable
          placeholder="Any tag"
          style="width: 170px"
          :input-props="{ 'aria-label': 'Tag' }"
        />
        <NSelect
          v-model:value="weather"
          :options="weatherOptions"
          clearable
          filterable
          placeholder="Any weather"
          style="width: 150px"
          :input-props="{ 'aria-label': 'Weather' }"
        />
        <NCheckbox v-model:checked="showHidden">Show hidden</NCheckbox>
      </NSpace>
      <ResourceTable
        :rows="rows"
        :columns="columns"
        :search-fields="['name', 'key', 'find']"
        what="tracks"
        :loading="loading"
        :error="error"
        :render-expand-icon="expandIcon"
        @retry="list.reload()"
      >
        <template #toolbar>
          <NButton type="primary" :disabled="!loaded" @click="trackEditor?.start(null)">Add track</NButton>
        </template>
      </ResourceTable>
    </NCard>
    <TrackEditor ref="trackEditor" @saved="list.replace" />
    <VariantEditor ref="variantEditor" @saved="replaceVariant" />
  </section>
</template>

<style scoped>
.filters {
  margin-bottom: 12px;
}
:deep(.flag) {
  margin-left: 6px;
}
:deep(.expander) {
  background: none;
  border: none;
  color: inherit;
  font-size: 18px;
  line-height: 1;
  cursor: pointer;
  padding: 0 4px;
}
:deep(.expander:focus-visible) {
  outline: 2px solid currentColor;
}
:deep(.muted) {
  opacity: 0.65;
}
</style>
