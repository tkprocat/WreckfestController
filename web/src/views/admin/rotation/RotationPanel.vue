<script setup lang="ts">
import StatusBadge from '@/components/StatusBadge.vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, useId, watch } from 'vue'
import { NAlert, NButton, NInput, NSelect, NSkeleton, NSpace, useMessage } from 'naive-ui'
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

/**
 * What a page holding the panel collapsed needs to show: whether the rotation could be read,
 * its size, the cup that set it, unsaved edits, and the notice waiting inside, most urgent
 * first (a save that conflicted, a change on the server, the offer to save to the cup).
 */
export interface RotationState {
  /** `failed` can come after a successful load: `loaded` says a rotation is still shown. */
  status: 'loading' | 'failed' | 'ready'
  loaded: boolean
  tracks: number
  cupName: string | null
  dirty: boolean
  notice: 'conflict' | 'stale' | 'offer' | null
}
const emit = defineEmits<{ state: [state: RotationState] }>()

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
/**
 * Set after a save while a cup is active: the rotation as saved, to offer to the cup (or the
 * collection it follows) too. What the offer sends is this, never later edits; editing again
 * withdraws it.
 */
const savedForCup = shallowRef<{ name: string; tracks: Track[] } | null>(null)
/** The collection the active cup follows, as it was when the panel loaded: its version guards the save. */
const followed = shallowRef<Collection | null>(null)
const root = ref<HTMLElement | null>(null)
const nameHintId = useId()
const editId = useId()
const deployHeadingId = useId()
/** A change on the server while the draft had unsaved edits: say so, do not overwrite. */
const staleNotice = ref(false)
const deployId = ref<number | null>(null)

const dirty = computed(
  () => !!loop.value && (name.value !== loop.value.collectionName || JSON.stringify(tracks.value) !== JSON.stringify(loop.value.tracks)),
)

// Loads are numbered, and edits counted: an answer is used only if it is the latest load's,
// and it replaces the draft only if nothing was typed while it was on its way.
let loadNumber = 0
let edits = 0
let adopting = false
watch([name, tracks], () => {
  if (!adopting) {
    edits++
    savedForCup.value = null
  }
}, { deep: true })

function adopt(next: Loop) {
  adopting = true
  loop.value = next
  name.value = next.collectionName
  tracks.value = next.tracks.map((t) => ({ ...t }))
  staleNotice.value = false
  void nextTick(() => (adopting = false))
}

/**
 * Reads the rotation and the active cup. `keepDraft` (a hub event): unsaved edits stay, with
 * a notice when the file changed. Without it (Reload, Discard) the draft is replaced, unless
 * something was typed after the click.
 */
async function load(options: { keepDraft?: boolean } = {}) {
  const mine = ++loadNumber
  const editsAtStart = edits
  loading.value = true
  try {
    const [rotation, current, variantList, collectionList] = await Promise.all([
      api.GET('/api/config/tracks'),
      api.GET('/api/cups/current'),
      variants.value.length ? Promise.resolve(null) : api.GET('/api/catalogue/variants', { params: { query: { includeHidden: true } } }),
      api.GET('/api/collections'),
    ])
    if (mine !== loadNumber) {
      return
    }

    if (!rotation.data) {
      loadError.value = rotation.error && typeof rotation.error === 'object' && 'title' in rotation.error ? String(rotation.error.title) : 'The rotation could not be read.'
      return
    }

    // Held here until this load is known to be the latest: an older answer must not change
    // the cup, or the collection a save to it would write.
    const activeCup = current.response.status === 200 && current.data ? current.data : null
    const followedNow = activeCup?.collectionId != null ? await readCollection(activeCup.collectionId) : null
    if (mine !== loadNumber) {
      return
    }

    loadError.value = null
    cup.value = activeCup
    followed.value = followedNow
    if (variantList?.data) variants.value = variantList.data
    collections.value = collectionList.data ?? collections.value

    const keep = edits !== editsAtStart || (options.keepDraft && dirty.value)
    if (!keep) {
      adopt(rotation.data)
    } else if (rotation.data.version !== loop.value?.version) {
      staleNotice.value = true
    }
  } catch {
    loadError.value = 'The rotation could not be read: no answer from the controller.'
  } finally {
    if (mine === loadNumber) {
      loading.value = false
    }
  }
}

async function readCollection(id: number): Promise<Collection | null> {
  try {
    const { data } = await api.GET('/api/collections/{id}', { params: { path: { id } } })
    return data ?? null
  } catch {
    return null
  }
}

const isLoop = (body: unknown): body is Loop => typeof body === 'object' && body !== null && 'version' in body && 'tracks' in body

function fail(outcome: Outcome<unknown>, fallback: string) {
  message.error(outcome.kind === 'no-answer' ? NO_ANSWER : 'message' in outcome ? outcome.message : fallback)
}

async function save() {
  if (busy.value || !loop.value || !name.value.trim()) return
  busy.value = 'save'
  // A load still on its way predates this save: its answer must not replace what it saves.
  loadNumber++
  loading.value = false
  const sent = { name: name.value.trim(), tracks: tracks.value.map((t) => ({ ...t })) }
  try {
    const outcome = await send(
      () =>
        api.PUT('/api/config/tracks', {
          body: { collectionName: sent.name, tracks: sent.tracks },
          headers: { 'If-Match': `"${loop.value!.version}"` },
        }),
      isLoop,
      'The rotation was not saved.',
    )
    if (outcome.kind === 'ok' && isLoop(outcome.row)) {
      // Loads started while the save was on its way read the file before it: not theirs to show.
      loadNumber++
      loading.value = false
      adopt(outcome.row)
      conflict.value = null
      await nextTick()
      savedForCup.value = cup.value !== null ? sent : null
      message.success('Rotation saved. The server uses it from its next start.')
    } else if (outcome.kind === 'conflict') {
      conflict.value = outcome.current
      await focusIn('[aria-label="Changed elsewhere"] button')
    } else {
      fail(outcome, 'The rotation was not saved.')
    }
  } finally {
    busy.value = null
  }
}

/** Their rotation wins: the draft shows it. Save is then disabled, so focus goes to the name. */
function useTheirs() {
  if (conflict.value) adopt(conflict.value)
  conflict.value = null
  void focusIn('input[aria-label="Rotation name"]')
}

/** Mine wins: my draft stays, on top of their version, to save again. */
function keepMine() {
  if (conflict.value) loop.value = conflict.value
  conflict.value = null
  void focusIn('[data-save]')
}

/** The conflict view replaces the editor, and back: keyboard focus follows. */
async function focusIn(selector: string) {
  await nextTick()
  root.value?.querySelector<HTMLElement>(selector)?.focus()
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
      message.success(`Deployed "${chosen.name}". The server uses it from its next start.`)
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
  const saved = savedForCup.value
  if (!target || !c || !saved || busy.value) return
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
    const outcome = target.kind === 'cup' ? await saveCupTracks(c, saved) : await saveCollectionTracks(saved)
    if (outcome.kind === 'ok') {
      savedForCup.value = null
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

/** PUT replaces everything a cup sets: send it back as it is, with the saved tracks. */
function saveCupTracks(c: Cup, saved: { name: string; tracks: Track[] }) {
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
    tracks: saved.tracks,
    collectionName: saved.name || null,
  }
  return send(
    () => api.PUT('/api/cups/{id}', { params: { path: { id: c.id } }, body, headers: { 'If-Match': `"${c.version}"` } }),
    (b: unknown): b is Cup => hasId(b) && 'startTime' in (b as object),
    'The cup was not saved.',
  )
}

/**
 * With the collection's version from when the panel loaded: a change made to it since is a
 * conflict, not something this save silently replaces.
 */
async function saveCollectionTracks(saved: { name: string; tracks: Track[] }): Promise<Outcome<Collection>> {
  const row = followed.value
  if (!row) {
    return { kind: 'refused', message: 'The collection could not be read. Reload, then try again.' }
  }

  const isCollection = (b: unknown): b is Collection => hasId(b) && 'tracks' in (b as object)
  return send(
    () =>
      api.PUT('/api/collections/{id}', {
        params: { path: { id: row.id } },
        body: { name: row.name, tracks: saved.tracks },
        headers: { 'If-Match': `"${row.version}"` },
      }),
    isCollection,
    'The collection was not saved.',
  )
}

watch(
  (): RotationState => ({
    status: loadError.value ? 'failed' : loop.value ? 'ready' : 'loading',
    loaded: loop.value !== null,
    tracks: tracks.value.length,
    cupName: cup.value?.name ?? null,
    dirty: dirty.value,
    notice: conflict.value ? 'conflict' : staleNotice.value ? 'stale' : savedForCup.value ? 'offer' : null,
  }),
  (state) => emit('state', state),
  { immediate: true },
)

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
  <div ref="root">
    <NAlert v-if="loadError" type="warning" :title="loadError" class="gap">
      <NButton size="small" @click="load()">Try again</NButton>
    </NAlert>
    <NSkeleton v-if="!loop && !loadError" text :repeat="4" aria-label="Loading the rotation" />
    <template v-if="loop">
      <div class="summary">
        <p class="source">
          <template v-if="cup">
            <StatusBadge compact tone="positive">Cup</StatusBadge>
            <span>Set by <strong>{{ cup.name }}</strong><template v-if="cup.activatedAt">, active since {{ formatWhen(cup.activatedAt) }}</template>.</span>
          </template>
          <template v-else>
            <StatusBadge compact tone="neutral">No cup</StatusBadge>
            <span>The server's own rotation.</span>
          </template>
        </p>
        <p class="muted next-start">
          This is server_config.cfg's rotation: the server uses it from its next start. Saving or deploying here does not
          change a race that is already running.
        </p>
      </div>

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
        <section class="form-section" :aria-labelledby="editId">
          <h3 :id="editId" class="form-section-title">Tracks</h3>
          <label class="name">
            <span class="name-label" aria-hidden="true">Rotation name</span>
            <NInput
              v-model:value="name"
              :maxlength="128"
              :disabled="busy !== null"
              placeholder="Rotation name"
              :status="name.trim() ? undefined : 'warning'"
              :input-props="{ 'aria-label': 'Rotation name', 'aria-describedby': name.trim() ? undefined : nameHintId, 'aria-invalid': name.trim() ? undefined : 'true' }"
            />
          </label>
          <p :id="nameHintId" class="hint" :class="{ hidden: name.trim() }">A name is needed: the server shows it to players.</p>
          <TrackListEditor v-model="tracks" :variants="variants" :disabled="busy !== null" />
          <div class="form-actions">
            <span class="form-actions-note" role="status">{{ dirty ? 'Unsaved changes.' : 'Matches server_config.cfg.' }}</span>
            <NButton :disabled="busy !== null || tracks.length < 2" @click="shuffle">Shuffle</NButton>
            <NButton :disabled="busy !== null" :loading="loading" @click="load()">{{ dirty ? 'Discard changes' : 'Reload' }}</NButton>
            <NButton type="primary" data-save :loading="busy === 'save'" :disabled="busy !== null || !dirty || !name.trim()" @click="save">Save rotation</NButton>
          </div>
          <NAlert v-if="savedForCup && cupTarget" type="info" class="gap" title="Saved to server_config.cfg">
            {{ cup?.name }} still has its own list for its next run. Save the same tracks to {{ cupTarget.label }} as well?
            <NSpace class="offer">
              <NButton size="small" type="primary" :loading="busy === 'cup'" :disabled="busy !== null" @click="saveToCup">Also save to {{ cupTarget.label }}</NButton>
              <NButton size="small" :disabled="busy !== null" @click="savedForCup = null">Only this session</NButton>
            </NSpace>
          </NAlert>
        </section>

        <section class="form-section" :aria-labelledby="deployHeadingId">
          <h3 :id="deployHeadingId" class="form-section-title">Deploy a collection</h3>
          <p class="form-section-help">Replaces the rotation above with a collection's tracks, from the next start.</p>
          <div class="deploy">
            <NSelect
              v-model:value="deployId"
              :options="collectionOptions"
              filterable
              clearable
              placeholder="Choose a collection..."
              v-select-focus="{ 'aria-label': 'Collection to deploy' }"
              :input-props="{ 'aria-label': 'Collection to deploy' }"
            />
            <NButton :loading="busy === 'deploy'" :disabled="busy !== null || deployId === null" @click="deploy">Deploy</NButton>
          </div>
        </section>
      </template>
    </template>
  </div>
</template>

<style scoped>
.gap {
  margin: 12px 0;
}
.summary {
  margin-bottom: var(--space-6);
}
.source {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  margin: 0;
}
.next-start {
  margin: 6px 0 0;
  font-size: var(--font-meta);
}
.name {
  display: flex;
  flex-direction: column;
  gap: 4px;
  max-width: 360px;
}
.name-label {
  font-size: var(--font-meta);
}
.offer {
  margin-top: 8px;
}
.hint {
  margin: 4px 0 12px;
  font-size: var(--font-meta);
  color: var(--text-secondary);
}
.hint.hidden {
  visibility: hidden;
}
.deploy {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}
.deploy > :first-child {
  flex: 1 1 220px;
  max-width: 360px;
}
</style>
