<script setup lang="ts">
import { computed, shallowRef } from 'vue'
import { NButton, NForm, NInput, NModal, NSelect, useMessage } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ConflictDialog from '@/crud/ConflictDialog.vue'
import FormField from '@/crud/FormField.vue'
import { NO_ANSWER, hasId, send, type Outcome } from '@/crud/outcome'
import { useResourceEditor } from '@/crud/useResourceEditor'
import { vSelectFocus } from '@/crud/selectFocus'
import { modalSize } from '@/crud/modal'

/**
 * Adds or edits a track: its key, name, origin and the weather it supports. Weather has its
 * own endpoint, so a save that changes it is two requests; if only the first lands, the
 * track is saved and the page says the weather was not. The weather is sent only when this
 * form changed it, with the version the first request answered with, so it never replaces
 * weather someone else set meanwhile.
 */
type Track = components['schemas']['TrackResponse']
type Origin = components['schemas']['TrackOrigin']
type Mod = components['schemas']['ModResponse']
interface TrackDraft {
  key: string
  name: string
  origin: Origin
  dlcName: string
  modId: number | null
  weather: string[]
}

const emit = defineEmits<{ saved: [track: Track] }>()
const message = useMessage()

const ORIGINS: { value: Origin; label: string }[] = [
  { value: 'BaseGame', label: 'Base game' },
  { value: 'Dlc', label: 'DLC' },
  { value: 'Workshop', label: 'Workshop' },
  { value: 'Custom', label: 'Custom' },
]

// What the pickers offer: loaded when the editor first opens.
const allWeather = shallowRef<string[]>([])
const mods = shallowRef<Mod[]>([])
let pickersLoaded = false
async function loadPickers() {
  if (pickersLoaded) return
  try {
    const [weather, modList] = await Promise.all([api.GET('/api/catalogue/weather'), api.GET('/api/catalogue/mods')])
    allWeather.value = weather.data ?? []
    mods.value = modList.data ?? []
    pickersLoaded = !!weather.data && !!modList.data
  } catch {
    // The pickers then offer only what the track already has.
  }
}

const isTrack = (body: unknown): body is Track => hasId(body) && 'variants' in body

const editor = useResourceEditor<Track, TrackDraft>({
  what: 'track',
  toDraft: (t) => ({
    key: t?.key ?? '',
    name: t?.name ?? '',
    origin: t?.origin ?? 'Custom',
    dlcName: t?.dlcName ?? '',
    modId: t?.mod?.id ?? null,
    // A new track supports every weather until told otherwise; the form shows that.
    weather: t ? [...t.weather] : [...allWeather.value],
  }),
  save: async (draft, version, t): Promise<Outcome<Track>> => {
    const body = {
      key: draft.key.trim(),
      name: draft.name.trim(),
      origin: draft.origin,
      dlcName: draft.origin === 'Dlc' ? draft.dlcName.trim() || null : null,
      modId: draft.origin === 'Workshop' ? draft.modId : null,
    }
    const outcome = await send(
      () =>
        t
          ? api.PUT('/api/catalogue/tracks/{id}', { params: { path: { id: t.id } }, body, headers: { 'If-Match': `"${version}"` } })
          : api.POST('/api/catalogue/tracks', { body }),
      isTrack,
      'The track was not saved.',
    )
    const weatherChanged: boolean = editor.changes.value.includes('weather')
    return outcome.kind === 'ok' && isTrack(outcome.row) && weatherChanged ? saveWeather(outcome.row, draft.weather) : outcome
  },
  saved: (t) => emit('saved', t),
})
const { draft, errors, saving, conflict } = editor

/** The second request, when the form changed the weather. The track is saved either way. */
async function saveWeather(track: Track, weather: string[]): Promise<Outcome<Track>> {
  const outcome = await send(
    () =>
      api.PUT('/api/catalogue/tracks/{id}/weather', {
        params: { path: { id: track.id } },
        body: { weather },
        headers: { 'If-Match': `"${track.version}"` },
      }),
    isTrack,
    'The weather was not saved.',
  )
  if (outcome.kind === 'ok' && isTrack(outcome.row)) {
    return outcome
  }

  if (outcome.kind === 'conflict') {
    message.error('The track was saved, but not its weather: someone else changed the track meanwhile. Open it again to see theirs.')
    return { kind: 'ok', row: outcome.current }
  }

  const reason = outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : 'The server refused it.'
  message.error(`The track was saved, but its weather was not: ${reason}`)
  return { kind: 'ok', row: track }
}

const builtIn = computed(() => editor.editing.value?.isBuiltIn ?? false)
const weatherOptions = computed(() =>
  [...new Set([...allWeather.value, ...draft.weather])].map((w) => ({ value: w, label: w })),
)
const modOptions = computed(() => mods.value.map((m) => ({ value: m.id, label: `${m.name} (${m.folderName})` })))

// Opening waits for the pickers: only the latest click opens, and never over an open form.
let opening = 0
async function start(track: Track | null) {
  const ticket = ++opening
  await loadPickers()
  if (ticket === opening && !editor.open.value) {
    editor.start(track)
  }
}

defineExpose({ start })
</script>

<template>
  <NModal
    :show="editor.open.value"
    preset="card"
    :title="editor.isNew.value ? 'Add track' : 'Edit track'"
    :aria-label="editor.isNew.value ? 'Add track' : 'Edit track'"
    v-bind="modalSize(560)"
    :mask-closable="!saving"
    @update:show="(show: boolean) => !show && editor.close()"
  >
    <ConflictDialog
      v-if="conflict"
      :mine="{ key: conflict.mine.key, name: conflict.mine.name, origin: conflict.mine.origin, dlcName: conflict.mine.dlcName, modId: conflict.mine.modId, weather: [...conflict.mine.weather].sort() }"
      :theirs="{ key: conflict.theirs.key, name: conflict.theirs.name, origin: conflict.theirs.origin, dlcName: conflict.theirs.dlcName ?? '', modId: conflict.theirs.mod?.id ?? null, weather: [...conflict.theirs.weather].sort() }"
      :labels="{ key: 'Key', name: 'Name', origin: 'Origin', dlcName: 'DLC', modId: 'Mod', weather: 'Weather' }"
      @theirs="editor.useTheirs()"
      @mine="editor.keepMine()"
    />
    <NForm v-else label-placement="top" :disabled="saving" @submit.prevent="editor.save()">
      <FormField v-slot="{ inputProps }" label="Name" field="name" :errors="errors">
        <NInput v-model:value="draft.name" :maxlength="128" :input-props="inputProps" />
      </FormField>
      <FormField v-slot="{ inputProps }" label="Key" field="key" :errors="errors">
        <NInput
          v-model:value="draft.key"
          :maxlength="64"
          :disabled="builtIn"
          :placeholder="builtIn ? '' : 'letters, digits and _'"
          :input-props="inputProps"
        />
      </FormField>
      <FormField v-slot="{ inputProps }" label="Origin" field="origin" :errors="errors">
        <NSelect v-model:value="draft.origin" :options="ORIGINS" filterable v-select-focus="inputProps" :input-props="inputProps" />
      </FormField>
      <FormField v-if="draft.origin === 'Dlc'" v-slot="{ inputProps }" label="DLC name" field="dlcName" :errors="errors">
        <NInput v-model:value="draft.dlcName" :maxlength="128" :input-props="inputProps" />
      </FormField>
      <FormField v-if="draft.origin === 'Workshop'" v-slot="{ inputProps }" label="Mod (optional)" field="modId" :errors="errors">
        <NSelect v-model:value="draft.modId" :options="modOptions" filterable clearable v-select-focus="inputProps" :input-props="inputProps" />
      </FormField>
      <FormField v-slot="{ inputProps }" label="Weather it supports" field="weather" :errors="errors">
        <NSelect v-model:value="draft.weather" :options="weatherOptions" multiple filterable v-select-focus="inputProps" :input-props="inputProps" />
      </FormField>
      <div class="form-actions">
        <NButton :disabled="saving" @click="editor.close()">Cancel</NButton>
        <NButton type="primary" attr-type="submit" :loading="saving" :disabled="saving">
          {{ editor.isNew.value ? 'Add' : 'Save' }}
        </NButton>
      </div>
    </NForm>
  </NModal>
</template>
