<script setup lang="ts">
import { computed, ref, shallowRef, useId } from 'vue'
import { NAlert, NButton, NModal, NTable } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { NO_ANSWER, send } from '@/crud/outcome'
import { modalSize } from '@/crud/modal'

/**
 * Uploads a WreckfestTrackDump tracks.json (`wftracks dump`) to fill the catalogue in, and
 * shows what the import did. Only the file's content is sent: the controller never reads
 * a path on this PC. The server adds what the catalogue lacks and leaves existing rows
 * alone, apart from track lengths (docs/API.md).
 */
type Report = components['schemas']['TrackDumpImportReport']
type Counts = components['schemas']['ImportCounts']

/** The server's limit (CatalogueImportController.MaxBodyBytes), checked here before uploading. */
const MAX_BYTES = 16 * 1024 * 1024

const emit = defineEmits<{ imported: [report: Report] }>()

const open = ref(false)
const file = shallowRef<File | null>(null)
const importing = ref(false)
const failure = ref<string | null>(null)
const report = shallowRef<Report | null>(null)
const inputId = `${useId()}-file`

function start() {
  if (importing.value) return
  file.value = null
  failure.value = null
  report.value = null
  open.value = true
}

function close() {
  if (!importing.value) open.value = false
}

function choose(event: Event) {
  const chosen = (event.target as HTMLInputElement).files?.[0] ?? null
  file.value = chosen
  report.value = null
  failure.value = chosen && chosen.size > MAX_BYTES ? `The file is over the ${MAX_BYTES / (1024 * 1024)} MB a track dump import takes.` : null
}

const isReport = (body: unknown): body is Report => typeof body === 'object' && body !== null && 'variants' in body

async function upload() {
  const chosen = file.value
  if (!chosen || importing.value || chosen.size > MAX_BYTES) return

  importing.value = true
  failure.value = null
  report.value = null
  try {
    let text: string
    try {
      text = await chosen.text()
    } catch {
      failure.value = 'The file could not be read.'
      return
    }

    // Sent as it is: the server parses it, so a large dump is not parsed twice.
    const outcome = await send(
      () =>
        api.POST('/api/catalogue/import/trackdump', {
          body: text,
          bodySerializer: (body: unknown) => body as string,
          headers: { 'Content-Type': 'application/json' },
        }),
      isReport,
      'The file was not imported.',
    )
    if (outcome.kind === 'ok' && isReport(outcome.row)) {
      report.value = outcome.row
      emit('imported', outcome.row)
    } else if (outcome.kind === 'no-answer') {
      failure.value = NO_ANSWER
    } else {
      failure.value = 'message' in outcome ? outcome.message : 'The file was not imported.'
    }
  } finally {
    importing.value = false
  }
}

const ROWS: { label: string; key: 'mods' | 'tracks' | 'variants' }[] = [
  { label: 'Mods', key: 'mods' },
  { label: 'Tracks', key: 'tracks' },
  { label: 'Variants', key: 'variants' },
]
const COLUMNS: { label: string; key: keyof Counts }[] = [
  { label: 'Added', key: 'added' },
  { label: 'Updated', key: 'updated' },
  { label: 'Unchanged', key: 'unchanged' },
  { label: 'Skipped', key: 'skipped' },
]

const skippedTotal = computed(() => (report.value ? ROWS.reduce((sum, row) => sum + report.value![row.key].skipped, 0) : 0))
const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`

/** One line for what happened, from what was added, updated and skipped. */
const summary = computed(() => {
  const r = report.value
  if (!r) return ''
  const added = r.mods.added + r.tracks.added + r.variants.added
  const updated = r.mods.updated + r.variants.updated
  const skipped = skippedTotal.value
  if (added === 0 && updated === 0) {
    return skipped === 0
      ? 'Nothing new: the catalogue already had everything in this file.'
      : `Nothing was imported: ${count(skipped, 'item was', 'items were')} skipped, listed below.`
  }

  const parts: string[] = []
  if (added > 0) {
    parts.push(
      `Added ${count(r.mods.added, 'mod', 'mods')}, ${count(r.tracks.added, 'track', 'tracks')} and ${count(r.variants.added, 'variant', 'variants')}.`,
    )
  }
  if (r.variants.updated > 0) parts.push(`Filled in ${count(r.variants.updated, 'track length', 'track lengths')}.`)
  if (r.mods.updated > 0) parts.push(`Filled in ${count(r.mods.updated, 'missing mod workshop id', 'missing mod workshop ids')}.`)
  if (skipped > 0) parts.push(`Skipped ${count(skipped, 'item', 'items')}, listed below.`)
  if (r.variants.added > 0) parts.push('New variants are not votable until you allow them.')
  return parts.join(' ')
})

defineExpose({ start })
</script>

<template>
  <NModal
    :show="open"
    preset="card"
    title="Import a track dump"
    aria-label="Import a track dump"
    v-bind="modalSize(720)"
    :mask-closable="!importing"
    :closable="!importing"
    @update:show="(show: boolean) => !show && close()"
  >
    <p class="help">
      Choose a <code>tracks.json</code> made by WreckfestTrackDump (<code>wftracks dump</code>). The import adds the mods, tracks and
      variants the catalogue does not have yet. It never changes ones already here, built-in or edited, except to fill in each
      variant's track length.
    </p>
    <form class="picker" @submit.prevent="upload()">
      <label :for="inputId" class="picker-label">Track dump file</label>
      <input
        :id="inputId"
        type="file"
        accept=".json,application/json"
        :disabled="importing"
        @change="choose"
      />
      <div class="form-actions">
        <NButton :disabled="importing" @click="close()">{{ report ? 'Close' : 'Cancel' }}</NButton>
        <NButton type="primary" attr-type="submit" :loading="importing" :disabled="importing || !file || (file?.size ?? 0) > MAX_BYTES">
          Import
        </NButton>
      </div>
    </form>

    <NAlert v-if="failure" type="error" :title="failure" class="gap" />

    <section v-if="report" class="result" aria-label="Import result">
      <p class="summary" role="status">{{ summary }}</p>
      <p v-if="report.tool || report.generatedAt" class="meta">
        {{ report.tool ?? 'Unknown tool' }}<template v-if="report.generatedAt">, dumped {{ report.generatedAt }}</template>
      </p>
      <NTable size="small" :bordered="false" class="counts">
        <thead>
          <tr>
            <th scope="col"><span class="visually-hidden">Kind</span></th>
            <th v-for="column in COLUMNS" :key="column.key" scope="col">{{ column.label }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in ROWS" :key="row.key">
            <th scope="row">{{ row.label }}</th>
            <td v-for="column in COLUMNS" :key="column.key" class="count">{{ report[row.key][column.key] }}</td>
          </tr>
        </tbody>
      </NTable>

      <template v-if="report.skipped.length > 0">
        <h3 class="result-heading">Skipped</h3>
        <p v-if="skippedTotal > report.skipped.length" class="meta">The first {{ report.skipped.length }} of {{ skippedTotal }}.</p>
        <ul class="list">
          <li v-for="(skip, index) in report.skipped" :key="index">
            <code class="row-id">{{ skip.id }}</code> {{ skip.reason }}
          </li>
        </ul>
      </template>

      <template v-if="report.diagnostics.length > 0">
        <h3 class="result-heading">What the dump reported</h3>
        <ul class="list">
          <li v-for="diagnostic in report.diagnostics" :key="diagnostic.code">
            <code class="row-id">{{ diagnostic.code }}</code> &times; {{ diagnostic.count }}<template v-if="diagnostic.severity === 'error'"> (error)</template>:
            {{ diagnostic.meaning }}
          </li>
        </ul>
      </template>
    </section>
  </NModal>
</template>

<style scoped>
.help,
.meta {
  color: var(--text-muted);
  font-size: var(--font-meta);
  margin: 0 0 var(--space-4);
}
.picker {
  display: flex;
  flex-direction: column;
  gap: var(--space-2);
}
.picker-label {
  font-size: var(--font-label);
}
.picker input {
  font-size: var(--font-control);
  max-width: 100%;
}
.gap {
  margin-top: var(--space-4);
}
.result {
  margin-top: var(--space-6);
}
.summary {
  font-size: var(--font-body);
  margin: 0 0 var(--space-2);
}
.counts {
  margin-bottom: var(--space-4);
}
.count {
  font-variant-numeric: tabular-nums;
}
.result-heading {
  font-size: var(--font-label);
  margin: var(--space-4) 0 var(--space-2);
}
.list {
  margin: 0;
  padding-left: var(--space-6);
  font-size: var(--font-meta);
  overflow-wrap: anywhere;
}
.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}
</style>
