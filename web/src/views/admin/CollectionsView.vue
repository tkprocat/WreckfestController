<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import { h, onMounted, ref, shallowRef } from 'vue'
import { NButton, NCard, NForm, NInput, NModal, NSpace, useMessage, type DataTableColumns } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ConflictDialog from '@/crud/ConflictDialog.vue'
import FormField from '@/crud/FormField.vue'
import ResourceTable from '@/crud/ResourceTable.vue'
import RowActionMenu from '@/crud/RowActionMenu.vue'
import TrackListEditor from '@/crud/TrackListEditor.vue'
import { NO_ANSWER, hasId, send, useConfirm, type Outcome } from '@/crud/outcome'
import { useResourceEditor } from '@/crud/useResourceEditor'
import { useResourceList } from '@/crud/useResourceList'
import { formatWhen } from '@/utils/format'
import { describeTrack as describe } from '@/utils/trackText'
import { modalSize } from '@/crud/modal'

type Summary = components['schemas']['CollectionSummaryResponse']
type Collection = components['schemas']['CollectionResponse']
type Track = components['schemas']['EventLoopTrack']
type Variant = components['schemas']['VariantResponse']
type Deployed = components['schemas']['DeployCollectionResponse']
interface CollectionDraft {
  name: string
  tracks: Track[]
}

const message = useMessage()
const confirm = useConfirm()

const list = useResourceList<Summary>(() => api.GET('/api/collections'), 'collections')
const { items: collections, loading, loaded, error } = list

// Hidden layouts too: a collection may still hold one, and its row needs a name.
const variants = shallowRef<Variant[]>([])
async function loadVariants() {
  try {
    const { data } = await api.GET('/api/catalogue/variants', { params: { query: { includeHidden: true } } })
    variants.value = data ?? []
  } catch {
    // Rows then show their raw ids; saving is unaffected.
  }
}

const isCollection = (body: unknown): body is Collection => hasId(body) && 'tracks' in body

const summaryOf = (c: Collection): Summary => ({
  id: c.id,
  name: c.name,
  version: c.version,
  trackCount: c.tracks.length,
  createdAt: c.createdAt,
  updatedAt: c.updatedAt,
})

/** The request's shape: the response's rows without their catalogue link. */
function toTrack({ variant: _variant, ...track }: Collection['tracks'][number]): Track {
  return track
}

const editor = useResourceEditor<Collection, CollectionDraft>({
  what: 'collection',
  toDraft: (c) => ({ name: c?.name ?? '', tracks: (c?.tracks ?? []).map(toTrack) }),
  save: (draft, version, c) => {
    const body = { name: draft.name.trim(), tracks: draft.tracks }
    return send(
      () =>
        c
          ? api.PUT('/api/collections/{id}', {
              params: { path: { id: c.id } },
              body,
              headers: { 'If-Match': `"${version}"` },
            })
          : api.POST('/api/collections', { body }),
      isCollection,
      'The collection was not saved.',
    )
  },
  saved: (c) => list.replace(summaryOf(c)),
})
const { draft, errors, saving, conflict } = editor

/** One row action at a time: opening, duplicating, deploying or deleting. */
const busy = ref<number | null>(null)

async function run<T>(id: number, action: () => Promise<T>): Promise<T | undefined> {
  if (busy.value !== null) {
    return undefined
  }

  busy.value = id
  try {
    return await action()
  } finally {
    busy.value = null
  }
}

function fail(outcome: Outcome<unknown>, fallback: string) {
  message.error(outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : fallback)
}

/** The list has no tracks: the editor opens on the collection as it is now. */
async function edit(row: Summary) {
  await run(row.id, async () => {
    const outcome = await send(() => api.GET('/api/collections/{id}', { params: { path: { id: row.id } } }), isCollection, 'The collection could not be opened.')
    if (outcome.kind === 'ok' && outcome.row) {
      list.replace(summaryOf(outcome.row))
      editor.start(outcome.row)
    } else {
      if (outcome.kind === 'refused') {
        void list.reload()
      }

      fail(outcome, 'The collection could not be opened.')
    }
  })
}

async function duplicate(row: Summary) {
  await run(row.id, async () => {
    const outcome = await send(
      () => api.POST('/api/collections/{id}/duplicate', { params: { path: { id: row.id } } }),
      isCollection,
      'The collection was not copied.',
    )
    if (outcome.kind === 'ok' && outcome.row) {
      list.replace(summaryOf(outcome.row))
      message.success(`Copied as "${outcome.row.name}".`)
    } else {
      fail(outcome, 'The collection was not copied.')
    }
  })
}

async function deploy(row: Summary) {
  if (busy.value !== null) {
    return
  }

  const sure = await confirm({
    title: 'Deploy collection',
    content: `Replace the server's rotation with "${row.name}" (${row.trackCount} tracks)? It is written to server_config.cfg; the server uses it from its next start.`,
    positive: 'Deploy',
  })
  if (!sure) {
    return
  }

  await run(row.id, async () => {
    // A deploy answers with a message, never with a row: no 409 here is a version conflict.
    const outcome = await send(
      () => api.POST('/api/collections/{id}/deploy', { params: { path: { id: row.id } } }),
      (_body): _body is Deployed => false,
      'The collection was not deployed.',
    )
    if (outcome.kind === 'ok' && outcome.row) {
      message.success(outcome.row.message)
    } else {
      fail(outcome, 'The collection was not deployed.')
    }
  })
}

async function remove(row: Summary) {
  if (busy.value !== null) {
    return
  }

  const sure = await confirm({
    title: 'Delete collection',
    content: `Delete "${row.name}"? Cups that use it keep its tracks as their own. This cannot be undone.`,
    positive: 'Delete',
  })
  if (!sure) {
    return
  }

  await run(row.id, async () => {
    const outcome = await send(() => api.DELETE('/api/collections/{id}', { params: { path: { id: row.id } } }), isCollection, 'The collection was not deleted.')
    if (outcome.kind === 'ok') {
      list.remove(row.id)
      message.success(`Collection "${row.name}" deleted.`)
    } else {
      fail(outcome, 'The collection was not deleted.')
    }
  })
}

const columns: DataTableColumns<Summary> = [
  { title: 'Name', key: 'name', sorter: (a, b) => a.name.localeCompare(b.name) },
  { title: 'Tracks', key: 'trackCount', width: 90, sorter: (a, b) => a.trackCount - b.trackCount },
  { title: 'Updated', key: 'updatedAt', render: (c) => formatWhen(c.updatedAt) },
  {
    title: 'Actions',
    key: 'actions',
    width: 215,
    render: (c) =>
      h(NSpace, { size: 'small', wrap: false }, () => [
        h(NButton, { size: 'small', loading: busy.value === c.id, disabled: busy.value !== null, onClick: () => void edit(c) }, () => 'Edit'),
        h(NButton, { size: 'small', type: 'primary', ghost: true, disabled: busy.value !== null || c.trackCount === 0, onClick: () => void deploy(c) }, () => 'Deploy'),
        h(RowActionMenu, {
          label: c.name,
          actionId: 'collection-' + c.id,
          disabled: busy.value !== null,
          options: [{ label: 'Duplicate', key: 'duplicate' }, { label: 'Delete', key: 'delete' }],
          onSelect: (key: string) => key === 'duplicate' ? void duplicate(c) : void remove(c),
        }),
      ]),
  },
]

onMounted(() => {
  void list.reload()
  void loadVariants()
})
</script>

<template>
  <section>
    <PageHeader title="Collections" description="Build reusable track rotations with game mode, laps and bots, then deploy them to the server configuration.">
      <template #actions>
        <NButton type="primary" :disabled="!loaded || busy !== null" @click="editor.start(null)">Add collection</NButton>
      </template>
    </PageHeader>
    <NCard>
      <ResourceTable
        :min-table-width="660"
        :rows="collections"
        :columns="columns"
        :search-fields="['name']"
        what="collections"
        :loading="loading"
        :error="error"
        @retry="list.reload()"
      />
    </NCard>

    <NModal
      :show="editor.open.value"
      preset="card"
      :title="editor.isNew.value ? 'Add collection' : 'Edit collection'"
      :aria-label="editor.isNew.value ? 'Add collection' : 'Edit collection'"
      v-bind="modalSize(820)"
      :mask-closable="!saving"
      @update:show="(show: boolean) => !show && editor.close()"
    >
      <ConflictDialog
        v-if="conflict"
        :mine="{ name: conflict.mine.name, tracks: conflict.mine.tracks.map(describe) }"
        :theirs="{ name: conflict.theirs.name, tracks: conflict.theirs.tracks.map(describe) }"
        :labels="{ name: 'Name', tracks: 'Tracks' }"
        @theirs="editor.useTheirs()"
        @mine="editor.keepMine()"
      />
      <NForm v-else label-placement="top" :disabled="saving" @submit.prevent="editor.save()">
        <FormField v-slot="{ inputProps }" label="Name" field="name" :errors="errors">
          <NInput v-model:value="draft.name" :maxlength="128" :input-props="inputProps" :disabled="saving" />
        </FormField>
        <FormField label="Tracks" field="tracks" :errors="errors">
          <TrackListEditor v-model="draft.tracks" :variants="variants" :disabled="saving" :errors="errors" style="width: 100%" />
        </FormField>
        <div class="form-actions">
          <NButton :disabled="saving" @click="editor.close()">Cancel</NButton>
        <NButton type="primary" attr-type="submit" :loading="saving" :disabled="saving">
            {{ editor.isNew.value ? 'Add' : 'Save' }}
          </NButton>
        </div>
      </NForm>
    </NModal>
  </section>
</template>
