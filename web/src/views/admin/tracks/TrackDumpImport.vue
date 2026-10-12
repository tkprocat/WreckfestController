<script setup lang="ts">
import { computed, ref, shallowRef, useId } from 'vue'
import { NAlert, NButton, NModal, NTable } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { NO_ANSWER, send } from '@/crud/outcome'
import { modalSize } from '@/crud/modal'
import { batches, dumpImageKeys, uploadKey } from '@/api/images'

/**
 * Uploads a WreckfestTrackDump tracks.json (`wftracks dump`) to fill the catalogue in, and
 * shows what the import did. Only the file's content is sent: the controller never reads
 * a path on this PC. The server adds what the catalogue lacks and leaves existing rows
 * alone, apart from track lengths (docs/API.md).
 *
 * Choosing the folder the dump was written to instead also imports the menu images
 * `wftracks imagedump` wrote there (#260): after the tracks.json, the small and large
 * images it names are sent in batches, each with the tracks.json.
 */
type Report = components['schemas']['TrackDumpImportReport']
type Counts = components['schemas']['ImportCounts']
type ImageReport = components['schemas']['TrackDumpImageReport']

/** The server's limit (CatalogueImportController.MaxBodyBytes), checked here before uploading. */
const MAX_BYTES = 16 * 1024 * 1024

/** One image batch, well under the server's 64 MB and 500 files (MaxImageBatchBytes, MaxImageBatchFiles). */
const BATCH_BYTES = 24 * 1024 * 1024
const BATCH_FILES = 200

const emit = defineEmits<{ imported: [report: Report] }>()

const open = ref(false)
const file = shallowRef<File | null>(null)
const importing = ref(false)
const failure = ref<string | null>(null)
const report = shallowRef<Report | null>(null)
const inputId = `${useId()}-file`
const folderId = `${useId()}-folder`

/** An image found in a chosen folder, with the path it had in it. */
interface FolderImage {
  file: File
  path: string
  size: number
}
const images = shallowRef<FolderImage[]>([])
const imageReport = shallowRef<ImageReport | null>(null)
const imageFailure = ref<string | null>(null)
const progress = ref<string | null>(null)

function start() {
  if (importing.value) return
  file.value = null
  images.value = []
  failure.value = null
  report.value = null
  imageReport.value = null
  imageFailure.value = null
  progress.value = null
  open.value = true
}

function close() {
  if (!importing.value) open.value = false
}

function choose(event: Event) {
  pick((event.target as HTMLInputElement).files?.[0] ?? null, [])
}

function pick(chosen: File | null, found: FolderImage[]) {
  file.value = chosen
  images.value = found
  report.value = null
  imageReport.value = null
  imageFailure.value = null
  failure.value = chosen && chosen.size > MAX_BYTES ? `The file is over the ${MAX_BYTES / (1024 * 1024)} MB a track dump import takes.` : null
}

/** A dump folder: its tracks.json (the one nearest the top), and the small and large images it names. */
async function chooseFolder(event: Event) {
  const all = [...((event.target as HTMLInputElement).files ?? [])]
  const path = (f: File) => f.webkitRelativePath || f.name
  const depth = (f: File) => path(f).split('/').length
  const dump = all.filter((f) => f.name.toLowerCase() === 'tracks.json').sort((a, b) => depth(a) - depth(b))[0]
  if (!dump) {
    pick(null, [])
    if (all.length > 0) failure.value = 'There is no tracks.json in that folder. Write the dump there: wftracks dump <game folder> -o <folder>/tracks.json.'
    return
  }

  let keys = new Set<string>()
  try {
    if (dump.size <= MAX_BYTES) keys = dumpImageKeys(JSON.parse(await dump.text()))
  } catch {
    // Not JSON: the server says what is wrong with it when it is imported.
  }

  const found = all
    .filter((f) => f !== dump)
    .map((f) => ({ file: f, path: path(f), size: f.size }))
    .filter((f) => {
      const key = uploadKey(f.path)
      return key !== null && keys.has(key)
    })
  pick(dump, found)
}

const isImageReport = (body: unknown): body is ImageReport => typeof body === 'object' && body !== null && 'stored' in body

/** Sends the folder's images in batches, adding up what each did. Stops at the first refusal. */
async function uploadImages(dump: File, found: FolderImage[]) {
  const total: ImageReport = { stored: 0, unchanged: 0, removed: 0, notUsed: 0, refused: 0, unmatched: 0 }
  let sent = 0
  for (const part of batches(found, BATCH_BYTES, BATCH_FILES)) {
    progress.value = `Importing images: ${sent} of ${found.length}`
    const form = new FormData()
    form.append('dump', dump, 'tracks.json')
    for (const image of part) form.append('images', image.file, image.path)
    const outcome = await send(
      () => api.POST('/api/catalogue/import/trackdump/images', { body: {} as never, bodySerializer: () => form }),
      isImageReport,
      'The images were not imported.',
    )
    if (outcome.kind !== 'ok' || !isImageReport(outcome.row)) {
      const reason = outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : 'The images were not imported.'
      imageFailure.value = sent === 0 ? reason : `Stopped after ${sent} of ${found.length} images: ${reason}`
      break
    }

    const row = outcome.row
    total.stored += row.stored
    total.unchanged += row.unchanged
    total.removed += row.removed
    total.notUsed += row.notUsed
    total.refused += row.refused
    // The same in every batch: it depends only on the dump and the catalogue.
    total.unmatched = row.unmatched
    sent += part.length
  }

  imageReport.value = sent > 0 ? total : null
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
      // After the tracks.json: images go only to tracks and variants the catalogue has.
      if (images.value.length > 0) await uploadImages(chosen, images.value)
      emit('imported', outcome.row)
    } else if (outcome.kind === 'no-answer') {
      failure.value = NO_ANSWER
    } else {
      failure.value = 'message' in outcome ? outcome.message : 'The file was not imported.'
    }
  } finally {
    importing.value = false
    progress.value = null
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

/** One line for what the image batches did. */
const imageSummary = computed(() => {
  const r = imageReport.value
  if (!r) return ''
  const parts = [`Images: ${r.stored} stored, ${r.unchanged} already here.`]
  if (r.removed > 0) parts.push(`Removed ${count(r.removed, 'image', 'images')} the dump no longer has.`)
  if (r.refused > 0) parts.push(`${count(r.refused, 'file was', 'files were')} not a PNG, or too large.`)
  if (r.unmatched > 0) parts.push(`${count(r.unmatched, 'track or variant', 'tracks and variants')} with images are not in the catalogue.`)
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
      <label :for="folderId" class="picker-label">Or a dump folder, to import the images too</label>
      <p class="meta">
        The folder holding <code>tracks.json</code> and what <code>wftracks imagedump</code> wrote. Only the small and large images
        are sent. The base game's images come from a dump of the game itself: the dedicated server has none.
      </p>
      <input :id="folderId" type="file" webkitdirectory :disabled="importing" @change="chooseFolder" />
      <p v-if="file && images.length > 0" class="meta" role="status">{{ file.name }} and {{ images.length }} images found.</p>
      <div class="form-actions">
        <NButton :disabled="importing" @click="close()">{{ report ? 'Close' : 'Cancel' }}</NButton>
        <NButton type="primary" attr-type="submit" :loading="importing" :disabled="importing || !file || (file?.size ?? 0) > MAX_BYTES">
          Import
        </NButton>
      </div>
    </form>

    <NAlert v-if="failure" type="error" :title="failure" class="gap" />
    <p v-if="progress" class="meta gap" role="status">{{ progress }}</p>

    <section v-if="report" class="result" aria-label="Import result">
      <p class="summary" role="status">{{ summary }}</p>
      <p v-if="imageSummary" class="summary">{{ imageSummary }}</p>
      <NAlert v-if="imageFailure" type="error" :title="imageFailure" class="gap" />
      <p v-if="report.tool || report.generatedAt" class="meta">
        {{ report.tool ?? 'Unknown tool' }}<template v-if="report.generatedAt">, dumped {{ report.generatedAt }}</template>
      </p>
      <!-- Scrolls inside the dialog on a phone rather than running past its edge. -->
      <div class="table-scroll">
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
      </div>

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
.table-scroll {
  overflow-x: auto;
}

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
