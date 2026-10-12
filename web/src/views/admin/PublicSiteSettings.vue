<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { NAlert, NButton, NCard, NForm, NFormItem, NInputNumber, useMessage } from 'naive-ui'
import { api } from '@/api/client'
import { fieldErrors, problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'

type PublicSiteSettings = components['schemas']['PublicSiteSettingsResponse']

const NO_ANSWER = 'No answer from the controller, so it is not known whether this was saved. Reload before trying again.'

const message = useMessage()

// As for voting: Save sends only a changed value, with the loaded version as If-Match.
const loaded = ref<PublicSiteSettings | null>(null)
const feedItemCount = ref<number | null>(null)
const errors = ref<Record<string, string>>({})
const loadError = ref<string | null>(null)
const saving = ref(false)

const dirty = () => loaded.value != null && feedItemCount.value != null && feedItemCount.value !== loaded.value.feedItemCount

function show(settings: PublicSiteSettings) {
  loaded.value = settings
  feedItemCount.value = settings.feedItemCount
  errors.value = {}
}

async function load() {
  try {
    const { data, error } = await api.GET('/api/settings/publicSite')
    if (data) {
      show(data)
      loadError.value = null
    } else {
      loadError.value = problemMessage(error, 'The front page settings could not be loaded.')
    }
  } catch {
    loadError.value = 'The front page settings could not be loaded: no answer from the controller.'
  }
}

async function save() {
  if (!loaded.value || !dirty() || saving.value) {
    return
  }

  saving.value = true
  errors.value = {}
  try {
    const { data, error, response } = await api.PUT('/api/settings/publicSite', {
      body: { feedItemCount: feedItemCount.value },
      headers: { 'If-Match': `"${loaded.value.version}"` },
    })
    if (data) {
      show(data as PublicSiteSettings)
      message.success('Front page settings saved.')
    } else if (response.status === 409 && error && 'version' in (error as object)) {
      show(error as PublicSiteSettings)
      message.warning('These settings were changed elsewhere, so nothing was saved. They are reloaded: make your change again.')
    } else {
      errors.value = fieldErrors(error)
      message.error(problemMessage(error, 'The front page settings were not saved.'))
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

onMounted(() => void load())
</script>

<template>
  <NCard title="Front page">
    <NAlert v-if="loadError" type="warning" :title="loadError" />
    <NForm v-else label-placement="top" class="form-page" :disabled="!loaded || saving" @submit.prevent="save">
      <NFormItem
        label="Items in recent races"
        :feedback="errors.feedItemCount ?? 'A race outside a cup is one item; a whole cup run, all its races, is one too.'"
        :validation-status="errors.feedItemCount ? 'error' : undefined"
      >
        <NInputNumber
          v-model:value="feedItemCount"
          :min="1"
          :max="50"
          :precision="0"
          :input-props="{ 'aria-label': 'Items in recent races' }"
        />
      </NFormItem>
      <div class="form-actions">
        <span class="form-actions-note">{{ dirty() ? 'Unsaved changes.' : 'No unsaved changes.' }}</span>
        <NButton :disabled="!dirty() || saving" @click="revert">Revert</NButton>
        <NButton type="primary" attr-type="submit" :loading="saving" :disabled="!dirty() || saving">Save</NButton>
      </div>
    </NForm>
  </NCard>
</template>
