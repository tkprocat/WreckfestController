<script setup lang="ts">
import { computed, shallowRef } from 'vue'
import {
  NButton,
  NCheckbox,
  NCheckboxGroup,
  NCollapse,
  NCollapseItem,
  NForm,
  NInput,
  NInputNumber,
  NModal,
  NRadioButton,
  NRadioGroup,
  NSelect,
  NSpace,
} from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ConflictDialog from '@/crud/ConflictDialog.vue'
import FormField from '@/crud/FormField.vue'
import TrackListEditor from '@/crud/TrackListEditor.vue'
import { hasId, send } from '@/crud/outcome'
import { vSelectFocus } from '@/crud/selectFocus'
import { useResourceEditor } from '@/crud/useResourceEditor'
import { timeZoneOptions } from '@/utils/timeZones'
import { GRID_ORDERS, SESSION_MODES } from '../configFields'

/**
 * Adds or edits a cup: when it starts and repeats, the rotation it deploys (a linked
 * collection, its own tracks, or the server's as they are), its scoring, and the server
 * settings it overrides. PUT replaces everything an admin sets, so the form holds all of it.
 */
type Cup = components['schemas']['CupResponse']
type Track = components['schemas']['EventLoopTrack']
type Variant = components['schemas']['VariantResponse']
type Summary = components['schemas']['CollectionSummaryResponse']
type ServerConfig = components['schemas']['EventServerConfig']

type Source = 'server' | 'collection' | 'own'
interface Overrides {
  serverName: string
  welcomeMessage: string
  password: string
  maxPlayers: number | null
  bots: number | null
  aiDifficulty: string | null
  laps: number | null
  vehicleDamage: string | null
  lobbyCountdown: number | null
}
interface CupDraft {
  name: string
  description: string
  /** The start as a datetime-local value, in the browser's time: "2026-10-02T20:00". */
  start: string
  timeZone: string
  repeat: 'none' | 'daily' | 'weekly'
  days: number[]
  time: string
  source: Source
  collectionId: number | null
  collectionName: string
  tracks: Track[]
  sessionMode: string | null
  gridOrder: string | null
  config: Overrides
}

const emit = defineEmits<{ saved: [cup: Cup] }>()

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday']
const plain = (...values: string[]) => values.map((v) => ({ label: v, value: v }))
const AI = plain('novice', 'amateur', 'expert')
const DAMAGE = plain('normal', 'intense', 'realistic', 'extreme')
const zones = timeZoneOptions()

const collections = shallowRef<Summary[]>([])
const variants = shallowRef<Variant[]>([])
async function loadPickers() {
  try {
    const [c, v] = await Promise.all([
      api.GET('/api/collections'),
      api.GET('/api/catalogue/variants', { params: { query: { includeHidden: true } } }),
    ])
    collections.value = c.data ?? collections.value
    variants.value = v.data ?? variants.value
  } catch {
    // The pickers then offer only what the cup already has.
  }
}

/** An ISO instant as a datetime-local value in the browser's time. */
function toLocalInput(iso: string): string {
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** A datetime-local value (browser time) as an ISO instant; null when empty or invalid. */
function toInstant(local: string): string | null {
  const d = new Date(local)
  return local && !Number.isNaN(d.getTime()) ? d.toISOString() : null
}

const blankOverrides = (): Overrides => ({
  serverName: '',
  welcomeMessage: '',
  password: '',
  maxPlayers: null,
  bots: null,
  aiDifficulty: null,
  laps: null,
  vehicleDamage: null,
  lobbyCountdown: null,
})

function toDraft(cup: Cup | null): CupDraft {
  const config = cup?.serverConfig
  return {
    name: cup?.name ?? '',
    description: cup?.description ?? '',
    start: cup ? toLocalInput(cup.startTime) : '',
    timeZone: cup?.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone,
    repeat: cup?.repeat ? (cup.repeat.frequency === 'daily' ? 'daily' : 'weekly') : 'none',
    days: [...(cup?.repeat?.days ?? [])],
    time: cup?.repeat?.time ?? '',
    source: cup?.collectionId != null ? 'collection' : cup?.tracks.length ? 'own' : 'server',
    collectionId: cup?.collectionId ?? null,
    // A linked collection's tracks come along, so switching to "its own" starts from them.
    collectionName: cup?.collectionId != null ? '' : (cup?.collectionName ?? ''),
    tracks: (cup?.tracks ?? []).map((t) => ({ ...t })),
    sessionMode: cup?.sessionMode ?? null,
    gridOrder: cup?.gridOrder ?? null,
    config: {
      ...blankOverrides(),
      ...Object.fromEntries(Object.entries(config ?? {}).filter(([, v]) => v !== null && v !== undefined)),
    },
  }
}

/** The overrides as the API takes them: blank is "not set". */
function toServerConfig(o: Overrides): ServerConfig | null {
  const text = (v: string) => (v.trim() === '' ? null : v.trim())
  const config: ServerConfig = {
    serverName: text(o.serverName),
    welcomeMessage: text(o.welcomeMessage),
    // A password is kept as typed: spaces may be part of it.
    password: o.password === '' ? null : o.password,
    maxPlayers: o.maxPlayers,
    bots: o.bots,
    aiDifficulty: o.aiDifficulty,
    laps: o.laps,
    vehicleDamage: o.vehicleDamage,
    lobbyCountdown: o.lobbyCountdown,
  }
  return Object.values(config).some((v) => v !== null) ? config : null
}

const isCup = (body: unknown): body is Cup => hasId(body) && 'startTime' in body

const editor = useResourceEditor<Cup, CupDraft>({
  what: 'cup',
  toDraft,
  save: (draft, version, cup) => {
    const body = {
      name: draft.name.trim(),
      description: draft.description.trim(),
      // Missing or invalid: sent as null, so the server names the field.
      startTime: toInstant(draft.start),
      timeZone: draft.timeZone,
      repeat: draft.repeat === 'none' ? null : { frequency: draft.repeat, days: draft.repeat === 'weekly' ? draft.days : null, time: draft.time },
      serverConfig: toServerConfig(draft.config),
      sessionMode: draft.sessionMode,
      gridOrder: draft.gridOrder,
      collectionId: draft.source === 'collection' ? draft.collectionId : null,
      tracks: draft.source === 'own' ? draft.tracks : null,
      collectionName: draft.source === 'own' ? draft.collectionName.trim() || null : null,
    }
    return send(
      () =>
        cup
          ? api.PUT('/api/cups/{id}', { params: { path: { id: cup.id } }, body, headers: { 'If-Match': `"${version}"` } })
          : api.POST('/api/cups', { body }),
      isCup,
      'The cup was not saved.',
    )
  },
  saved: (cup) => emit('saved', cup),
})
const { draft, errors, saving, conflict } = editor

const collectionOptions = computed(() => collections.value.map((c) => ({ value: c.id, label: `${c.name} (${c.trackCount} tracks)` })))

/** What a conflict compares: everything the form sets, as text. */
function comparable(d: CupDraft) {
  return {
    name: d.name,
    description: d.description,
    start: d.start,
    timeZone: d.timeZone,
    repeat: d.repeat === 'none' ? 'no' : d.repeat === 'daily' ? `daily at ${d.time}` : `weekly (${[...d.days].sort().map((x) => DAYS[x]).join(', ')}) at ${d.time}`,
    rotation: d.source === 'collection' ? `collection ${collections.value.find((c) => c.id === d.collectionId)?.name ?? d.collectionId}` : d.source === 'own' ? d.tracks.map((t) => t.track).join(', ') : "the server's",
    scoring: `${d.sessionMode ?? 'server'} / ${d.gridOrder ?? 'server'}`,
    overrides: JSON.stringify(toServerConfig(d.config)),
  }
}
const LABELS = { name: 'Name', description: 'Description', start: 'Start', timeZone: 'Time zone', repeat: 'Repeats', rotation: 'Rotation', scoring: 'Session mode / grid', overrides: 'Server overrides' }

// Opening waits for the pickers: only the latest click opens, and never over an open form.
let opening = 0
async function start(cup: Cup | null) {
  const ticket = ++opening
  await loadPickers()
  if (ticket === opening && !editor.open.value) {
    editor.start(cup)
  }
}

defineExpose({ start })
</script>

<template>
  <NModal
    :show="editor.open.value"
    preset="card"
    :title="editor.isNew.value ? 'Add cup' : 'Edit cup'"
    :aria-label="editor.isNew.value ? 'Add cup' : 'Edit cup'"
    style="max-width: 860px"
    :mask-closable="!saving"
    @update:show="(show: boolean) => !show && editor.close()"
  >
    <ConflictDialog
      v-if="conflict"
      :mine="comparable(conflict.mine)"
      :theirs="comparable(toDraft(conflict.theirs))"
      :labels="LABELS"
      @theirs="editor.useTheirs()"
      @mine="editor.keepMine()"
    />
    <NForm v-else label-placement="top" :disabled="saving" @submit.prevent="editor.save()">
      <FormField v-slot="{ inputProps }" label="Name" field="name" :errors="errors">
        <NInput v-model:value="draft.name" :maxlength="128" :input-props="inputProps" />
      </FormField>
      <FormField v-slot="{ inputProps }" label="Description" field="description" :errors="errors">
        <NInput v-model:value="draft.description" type="textarea" :maxlength="2000" :autosize="{ minRows: 2, maxRows: 6 }" :input-props="inputProps" />
      </FormField>

      <NSpace :wrap="true" :size="24">
        <FormField v-slot="{ inputProps }" label="Starts (your local time)" field="startTime" :errors="errors">
          <input v-model="draft.start" type="datetime-local" class="native" :disabled="saving" v-bind="inputProps" />
        </FormField>
        <FormField v-slot="{ inputProps }" label="Time zone" field="timeZone" :errors="errors" help="The repeat's time is in this zone.">
          <NSelect v-model:value="draft.timeZone" :options="zones" filterable style="width: 260px" v-select-focus="inputProps" :input-props="inputProps" />
        </FormField>
      </NSpace>

      <FormField v-slot="{ controlProps }" label="Repeats" field="repeat.frequency" :errors="errors">
        <NRadioGroup v-model:value="draft.repeat" v-bind="controlProps">
          <NRadioButton value="none">Once</NRadioButton>
          <NRadioButton value="daily">Daily</NRadioButton>
          <NRadioButton value="weekly">Weekly</NRadioButton>
        </NRadioGroup>
      </FormField>
      <NSpace v-if="draft.repeat !== 'none'" :wrap="true" :size="24">
        <FormField v-if="draft.repeat === 'weekly'" v-slot="{ controlProps }" label="On" field="repeat.days" :errors="errors">
          <NCheckboxGroup v-model:value="draft.days" v-bind="controlProps" role="group">
            <NSpace>
              <NCheckbox v-for="(day, i) in DAYS" :key="i" :value="i" :label="day.slice(0, 3)" :aria-label="day" />
            </NSpace>
          </NCheckboxGroup>
        </FormField>
        <FormField v-slot="{ inputProps }" label="At" field="repeat.time" :errors="errors">
          <input v-model="draft.time" type="time" class="native" :disabled="saving" v-bind="inputProps" />
        </FormField>
      </NSpace>

      <FormField v-slot="{ controlProps }" label="Rotation" field="collectionId" :errors="errors">
        <NRadioGroup v-model:value="draft.source" v-bind="controlProps">
          <NRadioButton value="collection">A collection</NRadioButton>
          <NRadioButton value="own">Its own tracks</NRadioButton>
          <NRadioButton value="server">Leave the server's</NRadioButton>
        </NRadioGroup>
      </FormField>
      <FormField v-if="draft.source === 'collection'" v-slot="{ inputProps }" label="Collection" field="collectionId" :errors="errors" help="Its tracks as they are when the cup starts.">
        <NSelect v-model:value="draft.collectionId" :options="collectionOptions" filterable v-select-focus="inputProps" :input-props="inputProps" />
      </FormField>
      <template v-if="draft.source === 'own'">
        <FormField v-slot="{ inputProps }" label="Rotation name (optional)" field="collectionName" :errors="errors" help='Shown to players; "Cup: <name>" when empty.'>
          <NInput v-model:value="draft.collectionName" :maxlength="128" :input-props="inputProps" />
        </FormField>
        <FormField label="Tracks" field="tracks" :errors="errors">
          <TrackListEditor v-model="draft.tracks" :variants="variants" :disabled="saving" :errors="errors" style="width: 100%" />
        </FormField>
      </template>

      <NSpace :wrap="true" :size="24">
        <FormField v-slot="{ inputProps }" label="Session mode" field="sessionMode" :errors="errors" help="Empty keeps the server's.">
          <NSelect v-model:value="draft.sessionMode" :options="[...SESSION_MODES]" clearable filterable style="width: 260px" v-select-focus="inputProps" :input-props="inputProps" />
        </FormField>
        <FormField v-slot="{ inputProps }" label="Grid order" field="gridOrder" :errors="errors" help="Empty keeps the server's.">
          <NSelect v-model:value="draft.gridOrder" :options="[...GRID_ORDERS]" clearable filterable style="width: 260px" v-select-focus="inputProps" :input-props="inputProps" />
        </FormField>
      </NSpace>

      <NCollapse class="gap">
        <NCollapseItem title="Server overrides (empty keeps the server's)" name="overrides">
          <NSpace :wrap="true" :size="16">
            <FormField v-slot="{ inputProps }" label="Server name" field="serverConfig.serverName" :errors="errors">
              <NInput v-model:value="draft.config.serverName" :maxlength="256" style="width: 260px" :input-props="inputProps" />
            </FormField>
            <FormField v-slot="{ inputProps }" label="Welcome message" field="serverConfig.welcomeMessage" :errors="errors">
              <NInput v-model:value="draft.config.welcomeMessage" :maxlength="256" style="width: 260px" :input-props="inputProps" />
            </FormField>
            <FormField v-slot="{ inputProps }" label="Password" field="serverConfig.password" :errors="errors">
              <NInput v-model:value="draft.config.password" type="password" show-password-on="click" :maxlength="256" style="width: 200px" :input-props="{ ...inputProps, autocomplete: 'off' }" />
            </FormField>
            <FormField v-slot="{ inputProps }" label="Max players" field="serverConfig" :errors="errors">
              <NInputNumber v-model:value="draft.config.maxPlayers" :min="1" style="width: 120px" :input-props="inputProps" />
            </FormField>
            <FormField v-slot="{ inputProps }" label="AI bots" field="serverConfig" :errors="errors">
              <NInputNumber v-model:value="draft.config.bots" :min="0" style="width: 120px" :input-props="inputProps" />
            </FormField>
            <FormField v-slot="{ inputProps }" label="Laps" field="serverConfig" :errors="errors">
              <NInputNumber v-model:value="draft.config.laps" :min="1" style="width: 120px" :input-props="inputProps" />
            </FormField>
            <FormField v-slot="{ inputProps }" label="Lobby countdown (s)" field="serverConfig" :errors="errors">
              <NInputNumber v-model:value="draft.config.lobbyCountdown" :min="0" style="width: 140px" :input-props="inputProps" />
            </FormField>
            <FormField v-slot="{ inputProps }" label="AI difficulty" field="serverConfig.aiDifficulty" :errors="errors">
              <NSelect v-model:value="draft.config.aiDifficulty" :options="AI" clearable filterable style="width: 160px" v-select-focus="inputProps" :input-props="inputProps" />
            </FormField>
            <FormField v-slot="{ inputProps }" label="Vehicle damage" field="serverConfig.vehicleDamage" :errors="errors">
              <NSelect v-model:value="draft.config.vehicleDamage" :options="DAMAGE" clearable filterable style="width: 160px" v-select-focus="inputProps" :input-props="inputProps" />
            </FormField>
          </NSpace>
        </NCollapseItem>
      </NCollapse>

      <NSpace justify="end">
        <NButton :disabled="saving" @click="editor.close()">Cancel</NButton>
        <NButton type="primary" attr-type="submit" :loading="saving" :disabled="saving">
          {{ editor.isNew.value ? 'Add' : 'Save' }}
        </NButton>
      </NSpace>
    </NForm>
  </NModal>
</template>

<style scoped>
.native {
  font: inherit;
  color: inherit;
  background: transparent;
  border: 1px solid rgba(128, 128, 128, 0.4);
  border-radius: 3px;
  padding: 4px 8px;
  color-scheme: light dark;
}
.gap {
  margin-bottom: 16px;
}
</style>
