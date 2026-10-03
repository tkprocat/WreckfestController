<script setup lang="ts">
import { computed, ref, shallowRef } from 'vue'
import { NButton, NCheckbox, NForm, NInput, NModal, NSelect, useMessage } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ConflictDialog from '@/crud/ConflictDialog.vue'
import FormField from '@/crud/FormField.vue'
import { NO_ANSWER, hasId, send, type Outcome } from '@/crud/outcome'
import { useResourceEditor } from '@/crud/useResourceEditor'
import { vSelectFocus } from '@/crud/selectFocus'
import { modalSize } from '@/crud/modal'

/**
 * Adds a variant under a track, or edits one: its id (what the game loads), name, game mode
 * and tags. Tags have their own endpoint, so a save that changes them is two requests; if
 * only the first lands, the variant is saved and the page says the tags were not. Tags
 * are sent only when this form changed them, with the version the first request answered with. Whether
 * players can vote for it is set here for a new variant, and by the table's switch after.
 */
type Track = components['schemas']['TrackResponse']
type Variant = components['schemas']['VariantResponse']
type Mode = components['schemas']['GameMode']
type Tag = components['schemas']['TagResponse']
interface VariantDraft {
  variantId: string
  name: string
  gameMode: Mode
  allowedForVoting: boolean
  tags: string[]
}

const emit = defineEmits<{ saved: [variant: Variant] }>()
const message = useMessage()

const MODES: { value: Mode; label: string }[] = [
  { value: 'Racing', label: 'Racing' },
  { value: 'Derby', label: 'Derby' },
]

const allTags = shallowRef<Tag[]>([])
async function loadTags() {
  try {
    const { data } = await api.GET('/api/catalogue/tags')
    allTags.value = data ?? allTags.value
  } catch {
    // The picker then offers only the tags the variant already has.
  }
}

/** The track a new variant goes under. */
const track = ref<Track | null>(null)

const isVariant = (body: unknown): body is Variant => hasId(body) && 'variantId' in body

const editor = useResourceEditor<Variant, VariantDraft>({
  what: 'variant',
  toDraft: (v) => ({
    variantId: v?.variantId ?? '',
    name: v?.name ?? '',
    gameMode: v?.gameMode ?? 'Racing',
    allowedForVoting: v?.allowedForVoting ?? true,
    tags: (v?.tags ?? []).map((t) => t.slug),
  }),
  save: async (draft, version, v): Promise<Outcome<Variant>> => {
    const fields = { variantId: draft.variantId.trim(), name: draft.name.trim(), gameMode: draft.gameMode }
    const outcome = await send(
      () =>
        v
          ? api.PUT('/api/catalogue/variants/{id}', { params: { path: { id: v.id } }, body: fields, headers: { 'If-Match': `"${version}"` } })
          : api.POST('/api/catalogue/variants', { body: { ...fields, trackId: track.value!.id, allowedForVoting: draft.allowedForVoting } }),
      isVariant,
      'The variant was not saved.',
    )
    const tagsChanged: boolean = editor.changes.value.includes('tags')
    return outcome.kind === 'ok' && isVariant(outcome.row) && tagsChanged ? saveTags(outcome.row, draft.tags) : outcome
  },
  saved: (v) => emit('saved', v),
})
const { draft, errors, saving, conflict } = editor

/** The second request, when the form changed the tags. The variant is saved either way. */
async function saveTags(variant: Variant, tags: string[]): Promise<Outcome<Variant>> {
  const outcome = await send(
    () =>
      api.PUT('/api/catalogue/variants/{id}/tags', {
        params: { path: { id: variant.id } },
        body: { tags },
        headers: { 'If-Match': `"${variant.version}"` },
      }),
    isVariant,
    'The tags were not saved.',
  )
  if (outcome.kind === 'ok' && isVariant(outcome.row)) {
    return outcome
  }

  if (outcome.kind === 'conflict') {
    message.error('The variant was saved, but not its tags: someone else changed the variant meanwhile. Open it again to see theirs.')
    return { kind: 'ok', row: outcome.current }
  }

  const reason = outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : 'The server refused them.'
  message.error(`The variant was saved, but its tags were not: ${reason}`)
  return { kind: 'ok', row: variant }
}

const builtIn = computed(() => editor.editing.value?.isBuiltIn ?? false)
const tagOptions = computed(() => {
  const known = new Map(allTags.value.map((t) => [t.slug, t.name]))
  for (const t of editor.editing.value?.tags ?? []) if (!known.has(t.slug)) known.set(t.slug, t.name)
  return [...known].map(([value, label]) => ({ value, label })).sort((a, b) => a.label.localeCompare(b.label))
})
const title = computed(() => (editor.isNew.value ? `Add a variant to ${track.value?.name ?? 'the track'}` : 'Edit variant'))

// Opening waits for the tags: only the latest click opens, and never over an open form.
let opening = 0
async function start(owner: Track, variant: Variant | null) {
  const ticket = ++opening
  await loadTags()
  if (ticket === opening && !editor.open.value) {
    track.value = owner
    editor.start(variant)
  }
}

defineExpose({ start })
</script>

<template>
  <NModal
    :show="editor.open.value"
    preset="card"
    :title="title"
    :aria-label="title"
    v-bind="modalSize(560)"
    :mask-closable="!saving"
    @update:show="(show: boolean) => !show && editor.close()"
  >
    <ConflictDialog
      v-if="conflict"
      :mine="{ variantId: conflict.mine.variantId, name: conflict.mine.name, gameMode: conflict.mine.gameMode, tags: [...conflict.mine.tags].sort() }"
      :theirs="{ variantId: conflict.theirs.variantId, name: conflict.theirs.name, gameMode: conflict.theirs.gameMode, tags: conflict.theirs.tags.map((t: Tag) => t.slug).sort() }"
      :labels="{ variantId: 'Variant id', name: 'Name', gameMode: 'Game mode', tags: 'Tags' }"
      @theirs="editor.useTheirs()"
      @mine="editor.keepMine()"
    />
    <NForm v-else label-placement="top" :disabled="saving" @submit.prevent="editor.save()">
      <FormField v-slot="{ inputProps }" label="Name" field="name" :errors="errors">
        <NInput v-model:value="draft.name" :maxlength="128" :input-props="inputProps" />
      </FormField>
      <FormField v-slot="{ inputProps }" label="Variant id" field="variantId" :errors="errors">
        <NInput
          v-model:value="draft.variantId"
          :maxlength="64"
          :disabled="builtIn"
          :placeholder="builtIn ? '' : 'the id the game loads: letters, digits and _'"
          :input-props="inputProps"
        />
      </FormField>
      <FormField v-slot="{ inputProps }" label="Game mode" field="gameMode" :errors="errors">
        <NSelect v-model:value="draft.gameMode" :options="MODES" filterable v-select-focus="inputProps" :input-props="inputProps" />
      </FormField>
      <FormField v-slot="{ inputProps }" label="Tags" field="tags" :errors="errors">
        <NSelect v-model:value="draft.tags" :options="tagOptions" multiple filterable v-select-focus="inputProps" :input-props="inputProps" />
      </FormField>
      <NCheckbox v-if="editor.isNew.value" v-model:checked="draft.allowedForVoting" class="gap">Players can vote for it</NCheckbox>
      <div class="form-actions">
        <NButton :disabled="saving" @click="editor.close()">Cancel</NButton>
        <NButton type="primary" attr-type="submit" :loading="saving" :disabled="saving">
          {{ editor.isNew.value ? 'Add' : 'Save' }}
        </NButton>
      </div>
    </NForm>
  </NModal>
</template>

<style scoped>
.gap {
  margin-bottom: 16px;
}
</style>
