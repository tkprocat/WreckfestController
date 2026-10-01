<script setup lang="ts">
import { h, onMounted, ref, watch } from 'vue'
import { NButton, NCard, NForm, NInput, NModal, NSpace, NTag, useMessage, type DataTableColumns } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ConflictDialog from '@/crud/ConflictDialog.vue'
import FormField from '@/crud/FormField.vue'
import ResourceTable from '@/crud/ResourceTable.vue'
import { NO_ANSWER, hasId, send, useConfirm } from '@/crud/outcome'
import { useResourceEditor } from '@/crud/useResourceEditor'
import { useResourceList } from '@/crud/useResourceList'
import { textOn } from '@/utils/color'

type Tag = components['schemas']['TagResponse']
interface TagDraft {
  name: string
  slug: string
  color: string
}

const message = useMessage()
const confirm = useConfirm()

const list = useResourceList<Tag>(() => api.GET('/api/catalogue/tags'), 'tags')
const { items: tags, loading, loaded, error } = list

const isTag = (body: unknown): body is Tag => hasId(body) && 'slug' in body

const editor = useResourceEditor<Tag, TagDraft>({
  what: 'tag',
  toDraft: (tag) => ({ name: tag?.name ?? '', slug: tag?.slug ?? '', color: tag?.color ?? '' }),
  save: (draft, _version, tag) => {
    const body = { name: draft.name.trim(), slug: draft.slug.trim(), color: draft.color.trim() || null }
    return send(
      () => (tag ? api.PUT('/api/catalogue/tags/{id}', { params: { path: { id: tag.id } }, body }) : api.POST('/api/catalogue/tags', { body })),
      isTag,
      'The tag was not saved.',
    )
  },
  saved: (tag) => list.replace(tag),
})
const { draft, errors, saving, conflict } = editor

// A new tag's slug follows its name until the slug is edited by hand.
const slugTouched = ref(false)
watch(
  () => draft.name,
  (name) => {
    if (editor.isNew.value && !slugTouched.value) {
      draft.slug = slugOf(name)
    }
  },
)

function slugOf(name: string): string {
  return name
    .toLowerCase()
    .normalize('NFKD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 64)
}

function openNew() {
  slugTouched.value = false
  editor.start(null)
}

/** Deleting a tag removes it from every variant: it asks first. */
const busy = ref<number | null>(null)

async function remove(tag: Tag) {
  if (busy.value !== null) {
    return
  }

  const sure = await confirm({
    title: 'Delete tag',
    content: `Delete the tag "${tag.name}"? It is removed from every track layout that has it. This cannot be undone.`,
    positive: 'Delete',
  })
  if (!sure) {
    return
  }

  busy.value = tag.id
  try {
    const outcome = await send(
      () => api.DELETE('/api/catalogue/tags/{id}', { params: { path: { id: tag.id } } }),
      isTag,
      'The tag was not deleted.',
    )
    if (outcome.kind === 'ok') {
      list.remove(tag.id)
      message.success(`Tag "${tag.name}" deleted.`)
    } else {
      message.error(outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : 'The tag was not deleted.')
    }
  } finally {
    busy.value = null
  }
}

const columns: DataTableColumns<Tag> = [
  {
    title: 'Tag',
    key: 'name',
    sorter: (a, b) => a.name.localeCompare(b.name),
    render: (tag) =>
      h(NTag, { size: 'small', color: tag.color ? { color: tag.color, textColor: textOn(tag.color) } : undefined }, () => tag.name),
  },
  { title: 'Slug', key: 'slug' },
  {
    title: 'Actions',
    key: 'actions',
    width: 180,
    render: (tag) =>
      h(NSpace, { size: 'small' }, () => [
        h(NButton, { size: 'small', disabled: busy.value !== null, onClick: () => editor.start(tag) }, () => 'Edit'),
        h(
          NButton,
          { size: 'small', type: 'error', ghost: true, loading: busy.value === tag.id, disabled: busy.value !== null, onClick: () => void remove(tag) },
          () => 'Delete',
        ),
      ]),
  },
]

onMounted(() => void list.reload())
</script>

<template>
  <section>
    <h1>Tags</h1>
    <NCard title="Tags">
      <p class="muted">Tags group track layouts, for filtering and for collections. Deleting a tag removes it from every layout.</p>
      <ResourceTable
        :rows="tags"
        :columns="columns"
        :search-fields="['name', 'slug']"
        what="tags"
        :loading="loading"
        :error="error"
        @retry="list.reload()"
      >
        <template #toolbar>
          <NButton type="primary" :disabled="!loaded" @click="openNew">Add tag</NButton>
        </template>
      </ResourceTable>
    </NCard>

    <NModal
      :show="editor.open.value"
      preset="card"
      :title="editor.isNew.value ? 'Add tag' : 'Edit tag'"
      :aria-label="editor.isNew.value ? 'Add tag' : 'Edit tag'"
      style="max-width: 520px"
      :mask-closable="!saving"
      @update:show="(show: boolean) => !show && editor.close()"
    >
      <ConflictDialog
        v-if="conflict"
        :mine="conflict.mine"
        :theirs="{ name: conflict.theirs.name, slug: conflict.theirs.slug, color: conflict.theirs.color ?? '' }"
        :labels="{ name: 'Name', slug: 'Slug', color: 'Colour' }"
        @theirs="editor.useTheirs()"
        @mine="editor.keepMine()"
      />
      <NForm v-else label-placement="left" label-width="auto" :disabled="saving" @submit.prevent="editor.save()">
        <FormField v-slot="{ inputProps }" label="Name" field="name" :errors="errors">
          <NInput v-model:value="draft.name" :maxlength="64" :input-props="inputProps" :disabled="saving" />
        </FormField>
        <FormField
          v-slot="{ inputProps }"
          label="Slug"
          field="slug"
          :errors="errors"
          help="Lowercase letters and digits, joined by hyphens. Used in links and filters."
        >
          <NInput
            v-model:value="draft.slug"
            :maxlength="64"
            :input-props="inputProps"
            :disabled="saving"
            @update:value="slugTouched = true"
          />
        </FormField>
        <FormField v-slot="{ inputProps }" label="Colour" field="color" :errors="errors" help="#RRGGBB, or empty for none.">
          <NInput v-model:value="draft.color" placeholder="#3366ff" :input-props="inputProps" :disabled="saving" />
        </FormField>
        <NSpace justify="end">
          <NButton :disabled="saving" @click="editor.close()">Cancel</NButton>
          <NButton type="primary" attr-type="submit" :loading="saving" :disabled="saving">
            {{ editor.isNew.value ? 'Add' : 'Save' }}
          </NButton>
        </NSpace>
      </NForm>
    </NModal>
  </section>
</template>

<style scoped>
h1 {
  margin-top: 0;
}

.muted {
  color: var(--text-muted);
  margin-top: 0;
}
</style>
