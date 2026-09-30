<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef } from 'vue'
import { NAlert, NButton, NInput, NSelect, NSkeleton, NSpace, NTag, useMessage } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import ConflictDialog from '@/crud/ConflictDialog.vue'
import TrackListEditor from '@/crud/TrackListEditor.vue'
import { NO_ANSWER, hasId, send, useConfirm, type Outcome } from '@/crud/outcome'
import { vSelectFocus } from '@/crud/selectFocus'
import { onHub } from '@/realtime/hub'
import { formatWhen } from '@/utils/format'
import { describeTrack } from '@/utils/trackText'

/**
 * What the server is running now: the rotation in server_config.cfg, and the cup that set
 * it, if one did. Edits save to the file (with its version, so a change made meanwhile is
 * not overwritten). While a cup is active, a save can also go to the cup, or to the
 * collection the cup follows, so the two do not drift apart.
 */
type Track = components['schemas']['EventLoopTrack']
type Loop = components['schemas']['EventLoopResponse']
type Cup = components['schemas']['CupResponse']
type Variant = components['schemas']['VariantResponse']
type Summary = components['schemas']['CollectionSummaryResponse']
type Collection = components['schemas']['CollectionResponse']

const message = useMessage()
const confirm = useConfirm()

const loop = shallowRef<Loop | null>(null)
const cup = shallowRef<Cup | null>(null)
const variants = shallowRef<Variant[]>([])
const collections = shallowRef<Summary[]>([])
const loadError = ref<string | null>(null)
const loading = ref(false)

// The draft: what the editor shows, and what a save sends.
const name = ref('')
const tracks = ref<Track[]>([])
const busy = ref<'save' | 'deploy' | 'cup' | null>(null)
const conflict = shallowRef<Loop | null>(null)
/** Set after a save while a cup is active: offer to save the same tracks there too. */
const offerCup = ref(false)
/** A change on the server while the draft had unsaved edits: say so, do not overwrite. */
const staleNotice = ref(false)
const deployId = ref<number | null>(null)

const dirty = computed(
  () => !!loop.value && (name.value !== loop.value.collectionName || JSON.stringify(tracks.value) !== JSON.stringify(loop.value.tracks)),
)

function adopt(next: Loop) {
  loop.value = next
  name.value = next.collectionName
  tracks.value = next.tracks.map((t) => ({ ...t }))
  staleNotice.value = false
}

async function load(options: { keepDraft?: boolean } = {}) {
  loading.value = true
  try {
    const [rotation, current, variantList, collectionList] = await Promise.all([
      api.GET('/api/config/tracks'),
      api.GET('/api/cups/current'),
      variants.value.length ? Promise.resolve(null) : api.GET('/api/catalogue/variants', { params: { query: { includeHidden: true } } }),
      api.GET('/api/collections'),
    ])
    if (!rotation.data) {
      loadError.value = rotation.error && typeof rotation.error === 'object' && 'title' in rotation.error ? String(rotation.error.title) : 'The rotation could not be read.'
      return
    }

    loadError.value = null
    cup.value = current.response.status === 200 && current.data ? current.data : null
    if (variantList?.data) variants.value = variantList.data
    collections.value = collectionList.data ?? collections.value
    if (options.keepDraft && dirty.value && rotation.data.version !== loop.value?.version) {
      staleNotice.value = true
    } else if (!(options.keepDraft && dirty.value)) {
      adopt(rotation.data)
    }
  } catch {
    loadError.value = 'The rotation could not be read: no answer from the controller.'
  } finally {
    loading.value = false
  }
}

const isLoop = (body: unknown): body is Loop => typeof body === 'object' && body !== null && 'version' in body && 'tracks' in body

function fail(outcome: Outcome<unknown>, fallback: string) {
  message.error(outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : fallback)
}

async function save() {
  if (busy.value || !loop.value) return
  busy.value = 'save'
  try {
    const outcome = await send(
      () =>
        api.PUT('/api/config/tracks', {
          body: { collectionName: name.value.trim(), tracks: tracks.value },
          headers: { 'If-Match': `"${loop.value!.version}"` },
        }),
      isLoop,
      'The rotation was not saved.',
    )
    if (outcome.kind === 'ok' && isLoop(outcome.row)) {
      adopt(outcome.row)
      conflict.value = null
      offerCup.value = cup.value !== null
      message.success('Rotation saved. The server uses it from its next start.')
    } else if (outcome.kind === 'conflict') {
      conflict.value = outcome.current
    } else {
      fail(outcome, 'The rotation was not saved.')
    }
  } finally {
    busy.value = null
  }
}

/** Their rotation wins: the draft shows it. */
function useTheirs() {
  if (conflict.value) adopt(conflict.value)
  conflict.value = null
}

/** Mine wins: my draft stays, on top of their version, to save again. */
function keepMine() {
  if (conflict.value) loop.value = conflict.value
  conflict.value = null
}

const comparable = (n: string, t: Track[]) => ({ name: n, tracks: t.map(describeTrack) })

function shuffle() {
  const next = [...tracks.value]
  for (let i = next.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1))
    ;[next[i], next[j]] = [next[j]!, next[i]!]
  }
  tracks.value = next
}

async function deploy() {
  const chosen = collections.value.find((c) => c.id === deployId.value)
  if (!chosen || busy.value) return
  const sure = await confirm({
    title: 'Deploy collection',
    content: `Replace the rotation with "${chosen.name}" (${chosen.trackCount} tracks)?${dirty.value ? ' Your unsaved changes here are dropped.' : ''}`,
    positive: 'Deploy',
  })
  if (!sure) return

  busy.value = 'deploy'
  try {
    const outcome = await send(
      () => api.POST('/api/collections/{id}/deploy', { params: { path: { id: chosen.id } } }),
      (_b): _b is never => false,
      'The collection was not deployed.',
    )
    if (outcome.kind === 'ok') {
      message.success(`Deployed "${chosen.name}".`)
      deployId.value = null
      await load()
    } else {
      fail(outcome, 'The collection was not deployed.')
    }
  } finally {
    busy.value = null
  }
}

/** Where "also save to the cup" writes: the cup's own tracks, or the collection it follows. */
const cupTarget = computed(() => {
  const c = cup.value
  if (!c) return null
  return c.collectionId != null ? { kind: 'collection' as const, label: `the collection "${c.collectionName}"`, id: c.collectionId } : { kind: 'cup' as const, label: `"${c.name}"`, id: c.id }
})

async function saveToCup() {
  const target = cupTarget.value
  const c = cup.value
  if (!target || !c || busy.value) return
  if (target.kind === 'collection') {
    const sure = await confirm({
      title: 'Save to the collection',
      content: `"${c.name}" follows ${target.label}. Saving changes that collection for everything that uses it. Save?`,
      positive: 'Save',
    })
    if (!sure) return
  }

  busy.value = 'cup'
  try {
    const outcome = target.kind === 'cup' ? await saveCupTracks(c) : await saveCollectionTracks(target.id)
    if (outcome.kind === 'ok') {
      offerCup.value = false
      message.success(`Saved to ${target.label} too.`)
      await load()
    } else if (outcome.kind === 'conflict') {
      message.warning(`${target.label} was changed meanwhile, so it was not saved. Check it on its page.`)
      await load()
    } else {
      fail(outcome, 'It was not saved.')
    }
  } finally {
    busy.value = null
  }
}

/** PUT replaces everything a cup sets: send it back as it is, with the new tracks. */
function saveCupTracks(c: Cup) {
  const body = {
    name: c.name,
    description: c.description,
    startTime: c.startTime,
    timeZone: c.timeZone,
    repeat: c.repeat,
    serverConfig: c.serverConfig,
    sessionMode: c.sessionMode,
    gridOrder: c.gridOrder,
    collectionId: null,
    tracks: tracks.value,
    collectionName: name.value.trim() || null,
  }
  return send(
    () => api.PUT('/api/cups/{id}', { params: { path: { id: c.id } }, body, headers: { 'If-Match': `"${c.version}"` } }),
    (b: unknown): b is Cup => hasId(b) && 'startTime' in (b as object),
    'The cup was not saved.',
  )
}

async function saveCollectionTracks(id: number): Promise<Outcome<Collection>> {
  const isCollection = (b: unknown): b is Collection => hasId(b) && 'tracks' in (b as object)
  const current = await send(() => api.GET('/api/collections/{id}', { params: { path: { id } } }), isCollection, 'The collection could not be read.')
  if (current.kind !== 'ok' || !current.row) return current
  const row = current.row
  return send(
    () =>
      api.PUT('/api/collections/{id}', {
        params: { path: { id } },
        body: { name: row.name, tracks: tracks.value },
        headers: { 'If-Match': `"${row.version}"` },
      }),
    isCollection,
    'The collection was not saved.',
  )
}

const collectionOptions = computed(() => collections.value.filter((c) => c.trackCount > 0).map((c) => ({ value: c.id, label: `${c.name} (${c.trackCount} tracks)` })))

const stops: (() => void)[] = []
onMounted(() => {
  void load()
  // A cup starting rewrites the rotation; a finished run may change which cup is active.
  stops.push(
    onHub('CupActivated', () => void load({ keepDraft: true })),
    onHub('CupOccurrenceEnded', () => void load({ keepDraft: true })),
  )
})
onBeforeUnmount(() => stops.forEach((stop) => stop()))
</script>

<template>
  <div>
    <NAlert v-if="loadError" type="warning" :title="loadError" class="gap">
      <NButton size="small" @click="load()">Try again</NButton>
    </NAlert>
    <NSkeleton v-if="!loop && !loadError" text :repeat="4" aria-label="Loading the rotation" />
    <template v-if="loop">
      <p class="source">
        <template v-if="cup">
          <NTag type="success" size="small">Cup</NTag>
          Set by <strong>{{ cup.name }}</strong><template v-if="cup.activatedAt">, active since {{ formatWhen(cup.activatedAt) }}</template>.
        </template>
        <template v-else>
          <NTag size="small">No cup</NTag>
          The server's own rotation.
        </template>
      </p>

      <NAlert v-if="staleNotice" type="info" class="gap" title="The rotation changed on the server">
        You have unsaved changes, so they are kept. Saving now asks what to do.
        <NButton size="small" @click="load()">Show the server's instead</NButton>
      </NAlert>

      <ConflictDialog
        v-if="conflict"
        :mine="comparable(name, tracks)"
        :theirs="comparable(conflict.collectionName, conflict.tracks)"
        :labels="{ name: 'Rotation name', tracks: 'Tracks' }"
        @theirs="useTheirs"
        @mine="keepMine"
      />
      <template v-else>
        <NInput v-model:value="name" :maxlength="128" placeholder="Rotation name (optional)" :input-props="{ 'aria-label': 'Rotation name' }" class="gap" style="max-width: 360px" />
        <TrackListEditor v-model="tracks" :variants="variants" :disabled="busy !== null" />
        <NSpace class="actions" align="center">
          <NButton type="primary" :loading="busy === 'save'" :disabled="busy !== null || !dirty" @click="save">Save rotation</NButton>
          <NButton :disabled="busy !== null || tracks.length < 2" @click="shuffle">Shuffle</NButton>
          <NButton :disabled="busy !== null" :loading="loading" @click="load()">{{ dirty ? 'Discard changes' : 'Reload' }}</NButton>
        </NSpace>
        <NAlert v-if="offerCup && cupTarget" type="info" class="gap">
          The rotation is saved. {{ cup?.name }} still has its own list for its next run.
          <NSpace class="offer">
            <NButton size="small" type="primary" :loading="busy === 'cup'" :disabled="busy !== null" @click="saveToCup">Also save to {{ cupTarget.label }}</NButton>
            <NButton size="small" :disabled="busy !== null" @click="offerCup = false">Only this session</NButton>
          </NSpace>
        </NAlert>
        <NSpace align="center" class="deploy">
          <NSelect
            v-model:value="deployId"
            :options="collectionOptions"
            filterable
            clearable
            placeholder="Deploy a collection..."
            style="width: 280px"
            v-select-focus="{ 'aria-label': 'Collection to deploy' }"
            :input-props="{ 'aria-label': 'Collection to deploy' }"
          />
          <NButton :loading="busy === 'deploy'" :disabled="busy !== null || deployId === null" @click="deploy">Deploy</NButton>
        </NSpace>
      </template>
    </template>
  </div>
</template>

<style scoped>
.gap {
  margin-bottom: 12px;
}
.source {
  display: flex;
  gap: 8px;
  align-items: center;
  margin: 0 0 12px;
}
.actions,
.deploy {
  margin: 12px 0;
}
.offer {
  margin-top: 8px;
}
</style>
