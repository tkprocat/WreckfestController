<script setup lang="ts">
import { h, onMounted, ref } from 'vue'
import { NButton, NCard, NSpace, NTag, useMessage, type DataTableColumns } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ResourceTable from '@/crud/ResourceTable.vue'
import { NO_ANSWER, hasId, send, useConfirm, type Outcome } from '@/crud/outcome'
import { useResourceList } from '@/crud/useResourceList'
import { formatWhen } from '@/utils/format'
import CupEditor from './cups/CupEditor.vue'

type Cup = components['schemas']['CupResponse']

const message = useMessage()
const confirm = useConfirm()

const list = useResourceList<Cup>(async () => {
  const { data, error } = await api.GET('/api/cups')
  return { data: data?.cups, error }
}, 'cups')
const { items: cups, loading, loaded, error } = list

const cupEditor = ref<InstanceType<typeof CupEditor> | null>(null)

/** One action at a time, on one cup. */
const busy = ref<number | null>(null)

async function run(id: number, action: () => Promise<void>) {
  if (busy.value !== null) return
  busy.value = id
  try {
    await action()
  } finally {
    busy.value = null
  }
}

function fail(outcome: Outcome<unknown>, fallback: string) {
  message.error(outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : fallback)
}

const isCup = (body: unknown): body is Cup => hasId(body) && 'startTime' in body

async function activate(cup: Cup) {
  if (busy.value !== null) return
  const sure = await confirm({
    title: 'Activate cup',
    content: `Start "${cup.name}" now? Its settings and rotation are written to server_config.cfg, players are warned, and the server restarts.`,
    positive: 'Activate',
  })
  if (!sure) return

  await run(cup.id, async () => {
    // Answers with a message; a 409 here is a refusal (already active, restart in progress).
    const outcome = await send(
      () => api.POST('/api/cups/{id}/activate', { params: { path: { id: cup.id } } }),
      (_b): _b is never => false,
      'The cup was not activated.',
    )
    if (outcome.kind === 'ok') {
      const answer = outcome.row as { message?: string } | undefined
      message.success(answer?.message ?? 'Activation started.')
    } else {
      fail(outcome, 'The cup was not activated.')
    }
  })
}

async function remove(cup: Cup) {
  if (busy.value !== null) return
  const sure = await confirm({
    title: 'Delete cup',
    content: `Delete "${cup.name}" and its schedule? This cannot be undone.`,
    positive: 'Delete',
  })
  if (!sure) return

  await run(cup.id, async () => {
    const outcome = await send(
      () => api.DELETE('/api/cups/{id}', { params: { path: { id: cup.id } }, headers: { 'If-Match': `"${cup.version}"` } }),
      isCup,
      'The cup was not deleted.',
    )
    if (outcome.kind === 'ok') {
      list.remove(cup.id)
      message.success(`"${cup.name}" deleted.`)
    } else if (outcome.kind === 'conflict') {
      // Changed since this page loaded it: show the change instead of deleting it unseen.
      list.replace(outcome.current)
      message.warning(`"${cup.name}" was changed meanwhile, so it was not deleted. Check it, then delete again if still wanted.`)
    } else {
      fail(outcome, 'The cup was not deleted.')
    }
  })
}

function rotationOf(cup: Cup): string {
  if (cup.collectionId != null) return `Collection: ${cup.collectionName || cup.collectionId}`
  if (cup.tracks.length) return `${cup.tracks.length} tracks of its own`
  return "The server's"
}

const OUTCOMES: Record<string, string> = { Activated: 'Ran', Failed: 'Failed', Cancelled: 'Cancelled', Missed: 'Missed' }

const columns: DataTableColumns<Cup> = [
  {
    title: 'Cup',
    key: 'name',
    sorter: (a, b) => a.name.localeCompare(b.name),
    render: (c) => h('span', [c.name, c.isActive ? h(NTag, { size: 'small', type: 'success', class: 'flag' }, () => 'Active') : null]),
  },
  { title: 'Next', key: 'nextOccurrence', render: (c) => (c.nextOccurrence ? formatWhen(c.nextOccurrence) : 'Not scheduled') },
  { title: 'Repeats', key: 'repeatDescription' },
  { title: 'Rotation', key: 'rotation', render: rotationOf },
  {
    title: 'Last run',
    key: 'lastOccurrence',
    render: (c) => (c.lastOccurrence ? `${formatWhen(c.lastOccurrence)}${c.lastOutcome ? ` (${OUTCOMES[c.lastOutcome] ?? c.lastOutcome})` : ''}` : ''),
  },
  {
    title: 'Actions',
    key: 'actions',
    width: 260,
    render: (c) =>
      h(NSpace, { size: 'small' }, () => [
        h(NButton, { size: 'small', disabled: busy.value !== null, 'aria-label': `Edit ${c.name}`, onClick: () => void cupEditor.value?.start(c) }, () => 'Edit'),
        h(
          NButton,
          { size: 'small', type: 'primary', ghost: true, loading: busy.value === c.id, disabled: busy.value !== null || c.isActive, 'aria-label': `Activate ${c.name}`, onClick: () => void activate(c) },
          () => 'Activate',
        ),
        h(NButton, { size: 'small', type: 'error', ghost: true, disabled: busy.value !== null, 'aria-label': `Delete ${c.name}`, onClick: () => void remove(c) }, () => 'Delete'),
      ]),
  },
]

onMounted(() => void list.reload())
</script>

<template>
  <section>
    <h1>Cups</h1>
    <NCard title="Cups">
      <p class="muted">
        Scheduled events: at their time the server restarts with the cup's rotation, scoring and settings.
      </p>
      <ResourceTable
        :rows="cups"
        :columns="columns"
        :search-fields="['name', 'description', 'collectionName']"
        what="cups"
        :loading="loading"
        :error="error"
        @retry="list.reload()"
      >
        <template #toolbar>
          <NButton type="primary" :disabled="!loaded" @click="cupEditor?.start(null)">Add cup</NButton>
        </template>
      </ResourceTable>
    </NCard>
    <CupEditor ref="cupEditor" @saved="list.replace" />
  </section>
</template>

<style scoped>
:deep(.flag) {
  margin-left: 6px;
}
</style>
