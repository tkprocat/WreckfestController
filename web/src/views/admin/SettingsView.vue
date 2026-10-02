<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import { computed, h, onMounted, reactive, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NDataTable,
  NForm,
  NFormItem,
  NInput,
  NInputNumber,
  NRadioButton,
  NRadioGroup,
  NSpace,
  NSwitch,
  useMessage,
  type DataTableColumns,
} from 'naive-ui'
import { api } from '@/api/client'
import { fieldErrors, problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'

type VoteSettings = components['schemas']['VoteSettingsResponse']
type Variant = components['schemas']['VariantResponse']
type Editable = Omit<VoteSettings, 'version'>

const NO_ANSWER = 'No answer from the controller, so it is not known whether this was saved. Reload before trying again.'

const message = useMessage()

// Voting. The form edits a copy; Save sends only the fields that differ from what was
// loaded, with its version as If-Match, so a change made elsewhere meanwhile is not undone.
const loaded = ref<VoteSettings | null>(null)
const form = reactive<Editable>({
  mode: 'Voting',
  directCooldownSeconds: 0,
  voteTimeoutSeconds: 1,
  maxLapsAllowed: 1,
  messageDelayMs: 0,
  suppressCommandsDuringRace: false,
})
const errors = ref<Record<string, string>>({})
const loadError = ref<string | null>(null)
const saving = ref(false)

const changes = computed<Partial<Editable>>(() => {
  const was = loaded.value
  if (!was) {
    return {}
  }

  return Object.fromEntries(
    (Object.keys(form) as (keyof Editable)[]).filter((key) => form[key] !== was[key]).map((key) => [key, form[key]]),
  ) as Partial<Editable>
})
const dirty = computed(() => Object.keys(changes.value).length > 0)

function show(settings: VoteSettings) {
  loaded.value = settings
  const { version: _, ...editable } = settings
  Object.assign(form, editable)
  errors.value = {}
}

async function loadVote() {
  try {
    const { data, error } = await api.GET('/api/settings/vote')
    if (data) {
      show(data)
      loadError.value = null
    } else {
      loadError.value = problemMessage(error, 'The voting settings could not be loaded.')
    }
  } catch {
    loadError.value = 'The voting settings could not be loaded: no answer from the controller.'
  }
}

async function save() {
  if (!loaded.value || !dirty.value || saving.value) {
    return
  }

  saving.value = true
  errors.value = {}
  try {
    const { data, error, response } = await api.PUT('/api/settings/vote', {
      body: changes.value,
      headers: { 'If-Match': `"${loaded.value.version}"` },
    })
    if (data) {
      show(data)
      message.success('Voting settings saved.')
    } else if (response.status === 409 && error && 'version' in (error as object)) {
      // Changed elsewhere (the desktop app, another admin) since this page loaded.
      show(error as VoteSettings)
      message.warning('These settings were changed elsewhere, so nothing was saved. They are reloaded: make your change again.')
    } else {
      errors.value = fieldErrors(error)
      message.error(problemMessage(error, 'The voting settings were not saved.'))
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

// Votable tracks: which catalogue variants players can vote for, one switch each.
const variants = ref<Variant[]>([])
const variantsError = ref<string | null>(null)
const search = ref('')
const pending = ref(new Set<number>())

const shown = computed(() => {
  const term = search.value.trim().toLowerCase()
  return term
    ? variants.value.filter((v) => [v.trackName, v.name, v.variantId].some((text) => text.toLowerCase().includes(term)))
    : variants.value
})
const votable = computed(() => variants.value.filter((v) => v.allowedForVoting).length)

async function loadVariants() {
  try {
    const { data, error } = await api.GET('/api/catalogue/variants')
    if (data) {
      variants.value = data
      variantsError.value = null
    } else {
      variantsError.value = problemMessage(error, 'The tracks could not be loaded.')
    }
  } catch {
    variantsError.value = 'The tracks could not be loaded: no answer from the controller.'
  }
}

function replace(row: Variant) {
  variants.value = variants.value.map((v) => (v.id === row.id ? row : v))
}

function isVariant(body: unknown): body is Variant {
  return typeof body === 'object' && body !== null && 'id' in body && 'allowedForVoting' in body
}

async function setVoting(variant: Variant, allowed: boolean) {
  if (pending.value.has(variant.id)) {
    return
  }

  pending.value.add(variant.id)
  try {
    const { data, error } = await api.PUT('/api/catalogue/variants/{id}/voting', {
      params: { path: { id: variant.id } },
      body: { allowed },
    })
    if (data) {
      replace(data)
    } else if (isVariant(error)) {
      // Changed elsewhere meanwhile: the 409 carries the row as it is now.
      replace(error)
      message.warning(`${variant.trackName} - ${variant.name} was changed elsewhere, so nothing was changed. It is shown as it is now.`)
    } else {
      message.error(problemMessage(error, `Voting for ${variant.trackName} - ${variant.name} was not changed.`))
    }
  } catch {
    message.error(`Voting for ${variant.trackName} - ${variant.name}: ${NO_ANSWER}`)
  } finally {
    pending.value.delete(variant.id)
  }
}

const columns: DataTableColumns<Variant> = [
  { title: 'Track', key: 'trackName', sorter: (a, b) => a.trackName.localeCompare(b.trackName) },
  { title: 'Layout', key: 'name' },
  { title: 'Mode', key: 'gameMode', width: 100 },
  {
    title: 'Votable',
    key: 'allowedForVoting',
    width: 100,
    render: (variant) =>
      h(NSwitch, {
        value: variant.allowedForVoting,
        loading: pending.value.has(variant.id),
        disabled: pending.value.has(variant.id),
        'aria-label': `Votable: ${variant.trackName} - ${variant.name}`,
        onUpdateValue: (allowed: boolean) => void setVoting(variant, allowed),
      }),
  },
]

onMounted(() => {
  void loadVote()
  void loadVariants()
})
</script>

<template>
  <section>
    <PageHeader title="Settings" description="Control how players vote and change tracks." />

    <NCard title="Voting" class="gap">
      <NAlert v-if="loadError" type="warning" :title="loadError" />
      <!-- Locked while saving: the answer replaces the form, so an edit made meanwhile
           would be lost. -->
      <NForm v-else label-placement="top" label-width="auto" :disabled="!loaded || saving" @submit.prevent="save">
        <NFormItem label="Track changes" :feedback="errors.mode" :validation-status="errors.mode ? 'error' : undefined">
          <NRadioGroup v-model:value="form.mode" name="mode" aria-label="Track changes">
            <NRadioButton value="Off">Off</NRadioButton>
            <NRadioButton value="Voting">Players vote</NRadioButton>
            <NRadioButton value="Direct">Direct</NRadioButton>
          </NRadioGroup>
        </NFormItem>
        <NFormItem
          label="Vote time (seconds)"
          :feedback="errors.voteTimeoutSeconds"
          :validation-status="errors.voteTimeoutSeconds ? 'error' : undefined"
        >
          <NInputNumber
            v-model:value="form.voteTimeoutSeconds"
            :min="1"
            :max="3600"
            :precision="0"
            :input-props="{ 'aria-label': 'Vote time (seconds)' }"
          />
        </NFormItem>
        <NFormItem
          label="Direct change cooldown (seconds)"
          :feedback="errors.directCooldownSeconds"
          :validation-status="errors.directCooldownSeconds ? 'error' : undefined"
        >
          <NInputNumber
            v-model:value="form.directCooldownSeconds"
            :min="0"
            :max="3600"
            :precision="0"
            :input-props="{ 'aria-label': 'Direct change cooldown (seconds)' }"
          />
        </NFormItem>
        <NFormItem
          label="Most laps a player may ask for"
          :feedback="errors.maxLapsAllowed"
          :validation-status="errors.maxLapsAllowed ? 'error' : undefined"
        >
          <NInputNumber
            v-model:value="form.maxLapsAllowed"
            :min="1"
            :max="999"
            :precision="0"
            :input-props="{ 'aria-label': 'Most laps a player may ask for' }"
          />
        </NFormItem>
        <NFormItem
          label="Pause between chat lines (ms)"
          :feedback="errors.messageDelayMs"
          :validation-status="errors.messageDelayMs ? 'error' : undefined"
        >
          <NInputNumber
            v-model:value="form.messageDelayMs"
            :min="0"
            :max="5000"
            :step="50"
            :precision="0"
            :input-props="{ 'aria-label': 'Pause between chat lines (ms)' }"
          />
        </NFormItem>
        <NFormItem label="Ignore chat commands during a race">
          <NSwitch v-model:value="form.suppressCommandsDuringRace" aria-label="Ignore chat commands during a race" />
        </NFormItem>
        <NSpace>
          <NButton type="primary" attr-type="submit" :loading="saving" :disabled="!dirty || saving">Save</NButton>
          <NButton :disabled="!dirty || saving" @click="revert">Revert</NButton>
        </NSpace>
      </NForm>
    </NCard>

    <NCard title="Votable tracks">
      <template #header-extra>
        <span class="muted">{{ votable }} of {{ variants.length }} votable</span>
      </template>
      <NAlert v-if="variantsError" type="warning" :title="variantsError" />
      <template v-else>
        <NInput v-model:value="search" placeholder="Search tracks and layouts" clearable class="gap" />
        <NDataTable :columns="columns" :data="shown" :row-key="(v: Variant) => v.id" :bordered="false" size="small" :pagination="{ pageSize: 25 }" />
      </template>
    </NCard>
  </section>
</template>

<style scoped>
.gap {
  margin-bottom: 16px;
}

.muted {
  color: var(--text-muted);
}
</style>
