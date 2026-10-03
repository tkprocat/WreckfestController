<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
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
import RowActionMenu from '@/crud/RowActionMenu.vue'
import { NO_ANSWER, hasId, send, useConfirm, type Outcome } from '@/crud/outcome'
import { useResourceList } from '@/crud/useResourceList'
import { textOn } from '@/utils/color'
import TrackEditor from './tracks/TrackEditor.vue'
import VariantEditor from './tracks/VariantEditor.vue'
import { vSelectFocus } from '@/crud/selectFocus'

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
const filtersActive = computed(() => !!(origin.value || mode.value || tag.value || weather.value || showHidden.value))

function resetFilters() {
  origin.value = null
  mode.value = null
  tag.value = null
  weather.value = null
  showHidden.value = false
}

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

/** The tracks before the other filters: hidden ones only when asked for. This is the table's total. */
const visibleTracks = computed(() => tracks.value.filter((t) => showHidden.value || !t.isHidden))
const emptyText = computed(() =>
  tracks.value.length > 0 && visibleTracks.value.length === 0 ? 'Every track is hidden. Tick "Show hidden" to see them.' : undefined,
)

const rows = computed<Row[]>(() =>
  visibleTracks.value
    .filter(
      (t) =>
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
    render: (v) => h('div', { class: 'row-identity' }, [
      h('span', { class: 'row-name' }, [v.name, v.isHidden ? h(NTag, { size: 'small', class: 'flag' }, () => 'Hidden') : null]),
      h('code', { class: 'row-id' }, v.variantId),
    ]),
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
    width: 200,
    render: (v) =>
      h(NSpace, { size: 'small', wrap: false }, () => [
        h(NButton, { size: 'small', disabled: disabled(), 'aria-label': 'Edit ' + v.name, onClick: () => void variantEditor.value?.start(track, v) }, () => 'Edit'),
        h(NButton, {
          size: 'small', disabled: disabled(), 'aria-label': (v.isHidden ? 'Unhide ' : 'Hide ') + v.name,
          onClick: () => void variantAction(v, v.isHidden ? 'unhide' : 'hide', '"' + v.name + '" ' + (v.isHidden ? 'is shown again.' : 'hidden.')),
        }, () => v.isHidden ? 'Unhide' : 'Hide'),
        h(RowActionMenu, {
          label: v.name,
          actionId: 'variant-' + v.id,
          disabled: disabled(),
          options: [{ label: v.isBuiltIn ? 'Reset' : 'Delete', key: v.isBuiltIn ? 'reset' : 'delete' }],
          onSelect: (key: string) => key === 'reset' ? void resetVariant(v) : void deleteVariant(v),
        }),
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
    render: (t) => h('div', { class: 'row-identity' }, [
      h('span', { class: 'row-name' }, [t.name, t.isHidden ? h(NTag, { size: 'small', class: 'flag' }, () => 'Hidden') : null]),
      h('code', { class: 'row-id' }, t.key),
    ]),
  },
  { title: 'Origin', key: 'origin', width: 120, render: (t) => (t.origin === 'Dlc' && t.dlcName ? `DLC: ${t.dlcName}` : t.mod ? `Workshop: ${t.mod.name}` : ORIGINS[t.origin]) },
  { title: 'Variants', key: 'variants', width: 90, render: (t) => String(variantsOf(t).length) },
  { title: 'Weather', key: 'weather', render: (t) => t.weather.join(', ') },
  {
    title: 'Actions',
    key: 'actions',
    width: 215,
    render: (t) =>
      h(NSpace, { size: 'small', wrap: false }, () => [
        h(NButton, { size: 'small', disabled: disabled(), 'aria-label': 'Edit ' + t.name, onClick: () => void trackEditor.value?.start(t) }, () => 'Edit'),
        h(NButton, { size: 'small', disabled: disabled(), 'aria-label': 'Add a variant to ' + t.name, onClick: () => void variantEditor.value?.start(t, null) }, () => 'Add variant'),
        h(RowActionMenu, {
          label: t.name,
          actionId: 'track-' + t.id,
          disabled: disabled(),
          options: [
            { label: t.isHidden ? 'Unhide' : 'Hide', key: 'visibility' },
            { label: t.isBuiltIn ? 'Reset' : 'Delete', key: t.isBuiltIn ? 'reset' : 'delete' },
          ],
          onSelect: (key: string) => {
            if (key === 'visibility') void trackAction(t, t.isHidden ? 'unhide' : 'hide', '"' + t.name + '" ' + (t.isHidden ? 'is shown again.' : 'hidden.'))
            else if (key === 'reset') void resetTrack(t)
            else void deleteTrack(t)
          },
        }),
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
    <PageHeader title="Tracks" description="Browse and edit tracks and layouts. Hidden ones stay out of votes and pickers.">
      <template #actions>
        <NButton type="primary" :disabled="!loaded" @click="trackEditor?.start(null)">Add track</NButton>
      </template>
    </PageHeader>
    <NCard>
      <ResourceTable
        :min-table-width="1080"
        :rows="rows"
        :total-rows="visibleTracks.length"
        :empty-text="emptyText"
        :filters-active="filtersActive"
        :columns="columns"
        :search-fields="['name', 'key', 'find']"
        what="tracks"
        :loading="loading"
        :error="error"
        :render-expand-icon="expandIcon"
        @retry="list.reload()"
        @reset-filters="resetFilters"
      >
        <template #filters>
          <NSpace class="filters" align="center">
            <NSelect
              v-model:value="origin"
              :options="Object.entries(ORIGINS).map(([value, label]) => ({ value, label }))"
              clearable
              filterable
              placeholder="Any origin"
              style="width: 150px"
              v-select-focus="{ 'aria-label': 'Origin' }"
              :input-props="{ 'aria-label': 'Origin' }"
            />
            <NSelect
              v-model:value="mode"
              :options="[{ value: 'Racing', label: 'Racing' }, { value: 'Derby', label: 'Derby' }]"
              clearable
              filterable
              placeholder="Any mode"
              style="width: 130px"
              v-select-focus="{ 'aria-label': 'Game mode' }"
              :input-props="{ 'aria-label': 'Game mode' }"
            />
            <NSelect
              v-model:value="tag"
              :options="tagOptions"
              clearable
              filterable
              placeholder="Any tag"
              style="width: 170px"
              v-select-focus="{ 'aria-label': 'Tag' }"
              :input-props="{ 'aria-label': 'Tag' }"
            />
            <NSelect
              v-model:value="weather"
              :options="weatherOptions"
              clearable
              filterable
              placeholder="Any weather"
              style="width: 150px"
              v-select-focus="{ 'aria-label': 'Weather' }"
              :input-props="{ 'aria-label': 'Weather' }"
            />
            <NCheckbox v-model:checked="showHidden">Show hidden</NCheckbox>
          </NSpace>
        </template>
      </ResourceTable>
    </NCard>
    <TrackEditor ref="trackEditor" @saved="list.replace" />
    <VariantEditor ref="variantEditor" @saved="replaceVariant" />
  </section>
</template>

<style scoped>
.filters {
  flex-wrap: wrap;
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
</style>
