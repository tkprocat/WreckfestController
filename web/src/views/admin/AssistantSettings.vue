<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { NAlert, NButton, NCard, NForm, NFormItem, NInput, NInputNumber, NSwitch, useMessage } from 'naive-ui'
import { api } from '@/api/client'
import { fieldErrors, problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'
import { useAssistantStatus } from '@/composables/useAssistantStatus'

type AssistantSettings = components['schemas']['AssistantSettingsResponse']

const NO_ANSWER = 'No answer from the controller, so it is not known whether this was saved. Reload before trying again.'

const message = useMessage()
const status = useAssistantStatus()

// As for the front page: Save sends only what changed, with the loaded version as If-Match.
// The key is never sent back by the API: the box starts empty, and a typed key replaces the
// stored one. Remove key sends an empty one.
const loaded = ref<AssistantSettings | null>(null)
const enabled = ref(false)
const model = ref('')
const baseUrl = ref('')
const dailyRequestLimit = ref<number | null>(null)
const apiKey = ref('')
const removeKey = ref(false)
const errors = ref<Record<string, string>>({})
const loadError = ref<string | null>(null)
const saving = ref(false)

const changes = computed(() => {
  const settings = loaded.value
  if (!settings) return {}
  const body: Record<string, unknown> = {}
  if (enabled.value !== settings.enabled) body.enabled = enabled.value
  if (model.value.trim() !== settings.model) body.model = model.value.trim()
  if (baseUrl.value.trim() !== settings.baseUrl) body.baseUrl = baseUrl.value.trim()
  if (dailyRequestLimit.value != null && dailyRequestLimit.value !== settings.dailyRequestLimit) body.dailyRequestLimit = dailyRequestLimit.value
  if (apiKey.value.trim()) body.apiKey = apiKey.value.trim()
  else if (removeKey.value) body.apiKey = ''
  return body
})

const dirty = computed(() => Object.keys(changes.value).length > 0)

const keyHelp = computed(() => {
  if (errors.value.apiKey) return errors.value.apiKey
  if (removeKey.value) return 'The key will be removed when you save.'
  return loaded.value?.apiKeySet
    ? 'A key is set. It is never shown again; type a new one to replace it.'
    : 'No key yet. The assistant stays off on the public site until one is set.'
})

function show(settings: AssistantSettings) {
  loaded.value = settings
  enabled.value = settings.enabled
  model.value = settings.model
  baseUrl.value = settings.baseUrl
  dailyRequestLimit.value = settings.dailyRequestLimit
  apiKey.value = ''
  removeKey.value = false
  errors.value = {}
}

async function load() {
  try {
    const { data, error } = await api.GET('/api/settings/assistant')
    if (data) {
      show(data)
      loadError.value = null
    } else {
      loadError.value = problemMessage(error, 'The AI assistant settings could not be loaded.')
    }
  } catch {
    loadError.value = 'The AI assistant settings could not be loaded: no answer from the controller.'
  }
}

async function save() {
  if (!loaded.value || !dirty.value || saving.value) {
    return
  }

  saving.value = true
  errors.value = {}
  try {
    const { data, error, response } = await api.PUT('/api/settings/assistant', {
      body: changes.value,
      headers: { 'If-Match': `"${loaded.value.version}"` },
    })
    if (data) {
      show(data as AssistantSettings)
      message.success('AI assistant settings saved.')
      void status.load()
    } else if (response.status === 409 && error && 'version' in (error as object)) {
      show(error as AssistantSettings)
      message.warning('These settings were changed elsewhere, so nothing was saved. They are reloaded: make your change again.')
    } else {
      errors.value = fieldErrors(error)
      message.error(problemMessage(error, 'The AI assistant settings were not saved.'))
    }
  } catch {
    message.error(NO_ANSWER)
  } finally {
    saving.value = false
  }
}

function markKeyForRemoval() {
  apiKey.value = ''
  removeKey.value = true
}

function revert() {
  if (loaded.value) {
    show(loaded.value)
  }
}

onMounted(() => void load())
</script>

<template>
  <NCard title="AI assistant">
    <NAlert v-if="loadError" type="warning" :title="loadError" />
    <NForm v-else label-placement="top" class="form-page" :disabled="!loaded || saving" @submit.prevent="save">
      <p class="form-section-help">
        Lets visitors ask about the server on the public AI page. The controller asks an OpenAI-compatible model with this key,
        and the model can only look up what the public pages show. Each question is billed to the key's account.
      </p>
      <NFormItem
        label="Show the AI page"
        :feedback="errors.enabled ?? 'Needs an API key as well.'"
        :validation-status="errors.enabled ? 'error' : undefined"
      >
        <NSwitch v-model:value="enabled" aria-label="Show the AI page" />
      </NFormItem>
      <NFormItem label="API key" :feedback="keyHelp" :validation-status="errors.apiKey ? 'error' : undefined">
        <div class="key-row">
          <NInput
            v-model:value="apiKey"
            type="password"
            show-password-on="click"
            :placeholder="loaded?.apiKeySet ? 'Key set; type to replace it' : 'sk-…'"
            :input-props="{ 'aria-label': 'API key', autocomplete: 'off' }"
            @update:value="removeKey = false"
          />
          <NButton v-if="loaded?.apiKeySet && !removeKey" @click="markKeyForRemoval">Remove key</NButton>
        </div>
      </NFormItem>
      <NFormItem
        label="Model"
        :feedback="errors.model ?? 'The model name as the API knows it, such as gpt-5-mini.'"
        :validation-status="errors.model ? 'error' : undefined"
      >
        <NInput v-model:value="model" :input-props="{ 'aria-label': 'Model' }" />
      </NFormItem>
      <NFormItem
        label="API address"
        :feedback="errors.baseUrl ?? 'OpenAI by default. Any OpenAI-compatible API works, such as OpenRouter or a local Ollama.'"
        :validation-status="errors.baseUrl ? 'error' : undefined"
      >
        <NInput v-model:value="baseUrl" :input-props="{ 'aria-label': 'API address' }" />
      </NFormItem>
      <NFormItem
        label="Questions per day"
        :feedback="errors.dailyRequestLimit ?? 'Across all visitors, to cap the bill. Each visitor can also ask at most 10 a minute.'"
        :validation-status="errors.dailyRequestLimit ? 'error' : undefined"
      >
        <NInputNumber
          v-model:value="dailyRequestLimit"
          :min="1"
          :max="100000"
          :precision="0"
          :input-props="{ 'aria-label': 'Questions per day' }"
        />
      </NFormItem>
      <div class="form-actions">
        <span class="form-actions-note">{{ dirty ? 'Unsaved changes.' : 'No unsaved changes.' }}</span>
        <NButton :disabled="!dirty || saving" @click="revert">Revert</NButton>
        <NButton type="primary" attr-type="submit" :loading="saving" :disabled="!dirty || saving">Save</NButton>
      </div>
    </NForm>
  </NCard>
</template>

<style scoped>
.key-row { display: flex; gap: 12px; width: 100%; }
@media (max-width: 760px) {
  .key-row { flex-direction: column; }
}
</style>
