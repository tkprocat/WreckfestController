<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NSelect,
  NSpace,
  NSwitch,
  NTabPane,
  NTabs,
  useMessage,
} from 'naive-ui'
import { api } from '@/api/client'
import { fieldErrors, problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'
import { FIELDS, SECTIONS, previewText, type ConfigField, type FieldDef, type ServerConfig } from './configFields'

type FieldInfo = components['schemas']['ConfigFieldResponse']
type Track = components['schemas']['EventLoopTrack']

const NO_ANSWER = 'No answer from the controller, so it is not known whether this was saved. Reload before trying again.'

const message = useMessage()

// The settings as loaded, and the form's copy. Save sends only the fields that differ.
const loaded = ref<ServerConfig | null>(null)
const form = reactive<ServerConfig>({})
const errors = ref<Record<string, string>>({})
const loadError = ref<string | null>(null)
const saving = ref(false)

// Which fields server_config.cfg can take: the rest are greyed out, with why.
const fieldInfo = ref<Record<string, FieldInfo>>({})

// The rotation, for the preview.
const rotation = ref<{ collectionName: string; tracks: Track[] } | null>(null)

const changes = computed<Partial<ServerConfig>>(() => {
  const was = loaded.value
  if (!was) {
    return {}
  }

  return Object.fromEntries(
    FIELDS.filter((def) => savable(def.field) && form[def.field] !== was[def.field]).map((def) => [def.field, form[def.field]]),
  ) as Partial<ServerConfig>
})
const dirty = computed(() => Object.keys(changes.value).length > 0)

const keys = computed(() => Object.fromEntries(Object.values(fieldInfo.value).map((f) => [f.field, f.key])))
const preview = computed(() => previewText(form, keys.value, rotation.value))

function savable(field: ConfigField): boolean {
  return fieldInfo.value[field]?.savable ?? true
}

function show(config: ServerConfig) {
  loaded.value = config
  Object.assign(form, config)
  errors.value = {}
}

async function load() {
  try {
    const [config, fields, tracks, name] = await Promise.all([
      api.GET('/api/config/basic'),
      api.GET('/api/config/basic/fields'),
      api.GET('/api/config/tracks'),
      api.GET('/api/config/tracks/collection-name'),
    ])
    if (!config.data) {
      loadError.value = problemMessage(config.error, 'The server settings could not be loaded.')
      return
    }

    show(config.data)
    loadError.value = null
    fieldInfo.value = Object.fromEntries((fields.data ?? []).map((f) => [f.field, f]))
    rotation.value = tracks.data ? { collectionName: name.data?.collectionName ?? '', tracks: tracks.data.tracks } : null
  } catch {
    loadError.value = 'The server settings could not be loaded: no answer from the controller.'
  }
}

async function save() {
  if (!dirty.value || saving.value) {
    return
  }

  saving.value = true
  errors.value = {}
  try {
    const { data, error } = await api.PUT('/api/config/basic', { body: changes.value })
    if (data) {
      show(data)
      message.success('Server settings saved. They apply when the server restarts.')
    } else {
      errors.value = fieldErrors(error)
      message.error(problemMessage(error, 'The server settings were not saved.'))
    }
  } catch {
    message.error(NO_ANSWER)
  } finally {
    saving.value = false
  }
}

function revert() {
  if (loaded.value) {
    show(loaded.value)
  }
}

/** A 0/1 setting as a switch. */
function flag(field: ConfigField): boolean {
  return form[field] === 1
}

function setFlag(field: ConfigField, on: boolean) {
  ;(form as Record<string, unknown>)[field] = on ? 1 : 0
}

function feedback(def: FieldDef): string | undefined {
  return errors.value[def.field] ?? (savable(def.field) ? def.help : fieldInfo.value[def.field]?.reason ?? undefined)
}

function status(def: FieldDef): 'error' | 'warning' | undefined {
  return errors.value[def.field] ? 'error' : savable(def.field) ? undefined : 'warning'
}

onMounted(() => void load())
</script>

<template>
  <section>
    <h1>Server config</h1>
    <NAlert v-if="loadError" type="warning" :title="loadError" />

    <NTabs v-else type="line" animated>
      <NTabPane name="settings" tab="Settings">
        <NAlert type="info" class="gap" :show-icon="false">
          These are written to server_config.cfg and apply when the server restarts. A greyed-out setting has no
          active line in the file; the note under it says what to change there.
        </NAlert>
        <NForm label-placement="left" label-width="auto" :disabled="!loaded || saving" @submit.prevent="save">
          <NCard v-for="section in SECTIONS" :key="section.title" :title="section.title" class="gap" size="small">
            <NFormItem
              v-for="def in section.fields"
              :key="def.field"
              :label="def.label"
              :feedback="feedback(def)"
              :validation-status="status(def)"
            >
              <NInput
                v-if="def.kind === 'text'"
                v-model:value="(form as Record<string, string>)[def.field]"
                :maxlength="def.max"
                :show-count="!!def.max"
                :disabled="!savable(def.field)"
                :input-props="{ 'aria-label': def.label }"
              />
              <NInputNumber
                v-else-if="def.kind === 'number'"
                v-model:value="(form as Record<string, number>)[def.field]"
                :min="def.min"
                :max="def.max"
                :precision="0"
                :disabled="!savable(def.field)"
                :input-props="{ 'aria-label': def.label }"
              />
              <NSelect
                v-else-if="def.kind === 'select'"
                v-model:value="(form as Record<string, string | number>)[def.field]"
                :options="[...def.options]"
                :disabled="!savable(def.field)"
                :aria-label="def.label"
              />
              <NSpace v-else align="center">
                <NSwitch
                  :value="flag(def.field)"
                  :disabled="!savable(def.field)"
                  :aria-label="def.label"
                  @update:value="(on: boolean) => setFlag(def.field, on)"
                />
                <span>{{ flag(def.field) ? def.on : '' }}</span>
              </NSpace>
            </NFormItem>
          </NCard>
          <NSpace>
            <NButton type="primary" attr-type="submit" :loading="saving" :disabled="!dirty || saving">Save</NButton>
            <NButton :disabled="!dirty || saving" @click="revert">Revert</NButton>
          </NSpace>
        </NForm>
      </NTabPane>

      <NTabPane name="preview" tab="Preview">
        <NAlert type="info" class="gap" :show-icon="false">
          server_config.cfg as these settings and the current rotation would write it, including unsaved changes.
        </NAlert>
        <pre class="preview">{{ preview }}</pre>
      </NTabPane>
    </NTabs>
  </section>
</template>

<style scoped>
h1 {
  margin-top: 0;
}

.gap {
  margin-bottom: 16px;
}

.preview {
  font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace;
  font-size: 0.85em;
  white-space: pre-wrap;
  padding: 12px;
  border-radius: 4px;
  background: rgba(127, 127, 127, 0.08);
  max-height: 70vh;
  overflow: auto;
}
</style>
