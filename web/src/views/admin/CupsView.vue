<script setup lang="ts">
import StatusBadge from '@/components/StatusBadge.vue'
import PageHeader from '@/components/PageHeader.vue'
import { h, onBeforeUnmount, onMounted, ref, useId } from 'vue'
import { NButton, NCard, NSpace, useMessage, type DataTableColumns } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ResourceTable from '@/crud/ResourceTable.vue'
import RowActionMenu from '@/crud/RowActionMenu.vue'
import { NO_ANSWER, hasId, send, useConfirm, type Outcome } from '@/crud/outcome'
import { useResourceList } from '@/crud/useResourceList'
import { formatWhen } from '@/utils/format'
import { onHub } from '@/realtime/hub'
import CupEditor from './cups/CupEditor.vue'
import RotationPanel, { type RotationState } from './rotation/RotationPanel.vue'

type Cup = components['schemas']['CupResponse']

const message = useMessage()
const confirm = useConfirm()

const list = useResourceList<Cup>(async () => {
  const { data, error } = await api.GET('/api/cups')
  return { data: data?.cups, error }
}, 'cups')
const { items: cups, loading, loaded, error } = list

const cupEditor = ref<InstanceType<typeof CupEditor> | null>(null)

/** The rotation editor starts collapsed; its header says what it holds and whether it needs a look. */
const rotation = ref<RotationState | null>(null)
const rotationOpen = ref(false)
const rotationId = useId()

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
      watchActivation(cup.id)
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

/** The active cup's tag: warming up until its start, then running. */
function activeTag(c: Cup) {
  if (!c.isActive) return null
  const label = c.phase === 'Warmup' ? 'Warmup' : 'Active'
  const title = c.phase === 'Warmup' && c.currentStart ? `Starts ${formatWhen(c.currentStart)}` : c.currentEnd ? `Ends ${formatWhen(c.currentEnd)}` : undefined
  return h(StatusBadge, { compact: true, tone: c.phase === 'Warmup' ? 'warning' : 'positive', class: 'flag', title }, () => label)
}

/** The next occurrence, with its warmup and end when it has them. */
function nextOf(c: Cup) {
  if (!c.nextOccurrence) return 'Not scheduled'
  const extra = [
    c.nextWarmup && c.nextWarmup !== c.nextOccurrence ? `warmup ${c.warmupTime}` : null,
    c.nextEnd ? `ends ${c.endTime}` : null,
  ].filter(Boolean)
  return extra.length ? `${formatWhen(c.nextOccurrence)} (${extra.join(', ')})` : formatWhen(c.nextOccurrence)
}

const columns: DataTableColumns<Cup> = [
  {
    title: 'Cup',
    key: 'name',
    sorter: (a, b) => a.name.localeCompare(b.name),
    render: (c) => h('span', [c.name, activeTag(c)]),
  },
  { title: 'Next', key: 'nextOccurrence', render: nextOf },
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
    width: 245,
    render: (c) =>
      h(NSpace, { size: 'small', wrap: false }, () => [
        h(NButton, { size: 'small', disabled: busy.value !== null, 'aria-label': 'Edit ' + c.name, onClick: () => void cupEditor.value?.start(c) }, () => 'Edit'),
        h(NButton, {
          size: 'small', type: 'primary', ghost: true, loading: busy.value === c.id,
          disabled: busy.value !== null || c.isActive, 'aria-label': 'Activate ' + c.name,
          onClick: () => void activate(c),
        }, () => 'Activate'),
        h(RowActionMenu, {
          label: c.name,
          actionId: 'cup-' + c.id,
          disabled: busy.value !== null,
          options: [{ label: 'Delete', key: 'delete' }],
          onSelect: () => void remove(c),
        }),
      ]),
  },
]

// Activation takes minutes: with players on, a five-minute warning, then up to ten more
// for the lobby, then the restart. The hub says when a cup became active or a run ended;
// polling covers a hub that is not connected, for twenty minutes.
const stops: (() => void)[] = []
let poll: ReturnType<typeof setInterval> | undefined

function watchActivation(id: number) {
  clearInterval(poll)
  let left = 120
  poll = setInterval(() => {
    void list.reload().then(() => {
      if (--left <= 0 || cups.value.some((c) => c.id === id && c.isActive)) clearInterval(poll)
    })
  }, 10_000)
}

onMounted(() => {
  void list.reload()
  stops.push(
    onHub('CupActivated', () => void list.reload()),
    onHub('CupStarted', () => void list.reload()),
    onHub('CupEnded', () => void list.reload()),
    onHub('CupOccurrenceEnded', () => void list.reload()),
  )
})

onBeforeUnmount(() => {
  stops.forEach((stop) => stop())
  clearInterval(poll)
})
</script>

<template>
  <section>
    <PageHeader title="Cups" description="Schedule race sessions with their own rotation, scoring and server settings.">
      <template #actions>
        <NButton type="primary" :disabled="!loaded" @click="cupEditor?.start(null)">Add cup</NButton>
      </template>
    </PageHeader>
    <!-- The same panel as the Rotation page, to compare with the cups. Collapsed with v-show,
         so it stays mounted: its summary is live, and collapsing never drops unsaved edits. -->
    <NCard class="gap" size="small">
      <div class="rotation-head">
        <NButton text :aria-expanded="rotationOpen" :aria-controls="rotationId" @click="rotationOpen = !rotationOpen">
          <span class="chevron" :class="{ open: rotationOpen }" aria-hidden="true">›</span>
          <span class="rotation-title">Configured rotation</span>
        </NButton>
        <!-- A failed reload keeps the draft and its notices: show them beside the failure. -->
        <span v-if="rotation" class="rotation-summary" role="status">
          <StatusBadge v-if="rotation.status === 'failed'" compact tone="negative">Could not be read</StatusBadge>
          <StatusBadge v-if="rotation.notice === 'conflict'" compact tone="negative">Changed elsewhere</StatusBadge>
          <StatusBadge v-else-if="rotation.notice === 'stale'" compact tone="warning">Changed on the server</StatusBadge>
          <StatusBadge v-else-if="rotation.notice === 'offer'" compact tone="neutral">Save to the cup too?</StatusBadge>
          <StatusBadge v-if="rotation.dirty" compact tone="warning">Unsaved changes</StatusBadge>
          <template v-if="rotation.loaded">
            {{ rotation.tracks }} {{ rotation.tracks === 1 ? 'track' : 'tracks' }} ·
            {{ rotation.cupName ? `set by ${rotation.cupName}` : "the server's own" }}
          </template>
          <template v-else-if="rotation.status === 'failed'">Open it to try again.</template>
          <template v-else>Loading…</template>
        </span>
      </div>
      <div v-show="rotationOpen" :id="rotationId" class="rotation-body">
        <RotationPanel @state="(state: RotationState) => (rotation = state)" />
      </div>
    </NCard>
    <NCard>
      <ResourceTable
        :min-table-width="980"
        :rows="cups"
        :columns="columns"
        :search-fields="['name', 'description', 'collectionName']"
        what="cups"
        :loading="loading"
        :error="error"
        @retry="list.reload()"
      />
    </NCard>
    <CupEditor ref="cupEditor" @saved="list.replace" />
  </section>
</template>

<style scoped>
.gap {
  margin-bottom: var(--space-6);
}
.rotation-head {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px 16px;
}
.rotation-title {
  font-weight: 600;
}
.chevron {
  display: inline-block;
  margin-right: 8px;
  transition: transform 0.15s;
}
.chevron.open {
  transform: rotate(90deg);
}
.rotation-body {
  margin-top: 16px;
}
.rotation-summary {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  color: var(--text-muted);
  font-size: var(--font-meta);
}
:deep(.flag) {
  margin-left: 6px;
}
</style>
