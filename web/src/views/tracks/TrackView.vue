<script setup lang="ts">
import { computed, ref, shallowRef, watch } from 'vue'
import { useRoute } from 'vue-router'
import { NAlert, NButton, NEmpty, NInputNumber, NSkeleton } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { variantImageUrl, variantLayoutUrl } from '@/api/images'
import RaceCard from '@/components/RaceCard.vue'
import StatusBadge from '@/components/StatusBadge.vue'
import { formatRaceTime, formatWhen } from '@/utils/format'
import { estimateLabel, lengthLabel, originLabel } from './trackText'

/**
 * One track's public page (#261): its picture, its layouts, and for the chosen layout what
 * the race log says. `/tracks/:track` shows the first layout; `/tracks/:track/:variant`
 * the one named, which is what other pages link to.
 */
type Track = components['schemas']['PublicTrackDetail']
type VariantDetail = components['schemas']['PublicVariantDetail']

const route = useRoute()
const key = computed(() => String(route.params.track ?? ''))
const variantParam = computed(() => (typeof route.params.variant === 'string' ? route.params.variant : ''))

const track = shallowRef<Track | null>(null)
const trackError = ref<string | null>(null)
const notFound = ref(false)
const loadingTrack = ref(true)

const detail = shallowRef<VariantDetail | null>(null)
const detailError = ref<string | null>(null)
const loadingDetail = ref(false)

/** For the estimate; not saved. */
const laps = ref<number | null>(5)

const failure = (status: number, what: string) =>
  status === 429 ? 'Too many requests. Wait a minute and try again.' : `${what} could not be loaded.`

let trackRequest = 0
/**
 * Loads the address's track. Another track's page is cleared first, so it never shows
 * under this address; only the latest request's answer counts.
 */
async function loadTrack() {
  const ticket = ++trackRequest
  if (track.value && track.value.key.toLowerCase() !== key.value.toLowerCase()) track.value = null
  loadingTrack.value = true
  trackError.value = null
  notFound.value = false
  try {
    const { data, response } = await api.GET('/api/public/tracks/{key}', { params: { path: { key: key.value } } })
    if (ticket !== trackRequest) return
    if (data) track.value = data
    else if (response.status === 404) {
      track.value = null
      notFound.value = true
    } else trackError.value = failure(response.status, 'The track')
  } catch {
    if (ticket === trackRequest) trackError.value = 'No answer from the server. Check your connection and try again.'
  } finally {
    if (ticket === trackRequest) loadingTrack.value = false
  }
}

/** The layout shown: the one the address names, else the first. Null for an id the track does not have. */
const variant = computed(() => {
  const variants = track.value?.variants ?? []
  if (!variantParam.value) return variants[0] ?? null
  return variants.find((v) => v.variantId.toLowerCase() === variantParam.value.toLowerCase()) ?? null
})

let detailRequest = 0
async function loadDetail(variantId: string) {
  const ticket = ++detailRequest
  loadingDetail.value = true
  detailError.value = null
  detail.value = null
  try {
    const { data, response } = await api.GET('/api/public/variants/{variantId}', { params: { path: { variantId } } })
    if (ticket !== detailRequest) return
    if (data) detail.value = data
    else detailError.value = failure(response.status, 'The race history')
  } catch {
    if (ticket === detailRequest) detailError.value = 'No answer from the server. Check your connection and try again.'
  } finally {
    if (ticket === detailRequest) loadingDetail.value = false
  }
}

watch(key, () => void loadTrack(), { immediate: true })
watch(
  () => variant.value?.variantId,
  (id) => {
    if (id) void loadDetail(id)
  },
  { immediate: true },
)

/** The large picture, else the small one scaled up: better than none. */
const pictureUrl = computed(() => {
  const v = variant.value
  if (v?.images.large) return variantImageUrl(v.variantId, 'Large', v.images.large)
  return v?.images.small ? variantImageUrl(v.variantId, 'Small', v.images.small) : null
})
const layoutUrl = computed(() => {
  const v = variant.value
  return v?.images.layout ? variantLayoutUrl(v.variantId, v.images.layout) : null
})

const workshopUrl = computed(() =>
  track.value?.workshopId ? `https://steamcommunity.com/sharedfiles/filedetails/?id=${encodeURIComponent(track.value.workshopId)}` : null,
)

const estimate = computed(() => estimateLabel(detail.value?.typicalLapMs, laps.value ?? 0))
const now = new Date()
const count = (n: number, one: string, many: string) => `${n} ${n === 1 ? one : many}`
</script>

<template>
  <section class="track-page spectator" :aria-busy="loadingTrack">
    <RouterLink :to="{ name: 'tracks' }" class="back">← All tracks</RouterLink>

    <div v-if="loadingTrack && !track" class="loading" aria-label="Loading track">
      <NSkeleton class="hero-skeleton" />
      <p role="status" class="visually-hidden">Loading track…</p>
    </div>

    <NEmpty v-else-if="notFound" class="missing" description="There is no track at this address.">
      <template #extra>
        <RouterLink :to="{ name: 'tracks' }">See every track</RouterLink>
      </template>
    </NEmpty>

    <NAlert v-else-if="trackError && !track" type="warning" :title="trackError">
      <NButton size="medium" @click="loadTrack()">Try again</NButton>
    </NAlert>

    <template v-else-if="track">
      <NAlert v-if="trackError" type="warning" :title="trackError" class="notice">
        Showing what was loaded before.
        <NButton size="medium" @click="loadTrack()">Try again</NButton>
      </NAlert>
      <header class="hero">
        <div class="picture">
          <img v-if="pictureUrl" :src="pictureUrl" alt="" />
          <img v-if="layoutUrl" class="layout" :src="layoutUrl" :alt="variant ? `Layout of ${variant.name}` : ''" />
          <span v-if="!pictureUrl && !layoutUrl" class="placeholder" aria-hidden="true">{{ track.name.charAt(0) }}</span>
        </div>
        <div class="title">
          <p class="eyebrow">{{ originLabel(track) }}</p>
          <h1>{{ track.name }}</h1>
          <a v-if="workshopUrl" :href="workshopUrl" target="_blank" rel="noopener noreferrer" class="workshop">Steam Workshop page</a>
        </div>
      </header>

      <nav class="layouts" aria-label="Layouts">
        <RouterLink
          v-for="v in track.variants"
          :key="v.variantId"
          :to="{ name: 'track', params: { track: track.key, variant: v.variantId } }"
          :aria-current="variant?.variantId === v.variantId ? 'page' : undefined"
          :class="{ current: variant?.variantId === v.variantId }"
        >{{ v.name }}</RouterLink>
      </nav>

      <NEmpty v-if="!variant" class="missing" :description="`${track.name} has no layout called ${variantParam}.`" />

      <section v-else class="variant" :aria-labelledby="'variant-title'">
        <div class="variant-head">
          <h2 id="variant-title">{{ variant.name }}</h2>
          <code class="variant-id">{{ variant.variantId }}</code>
        </div>

        <dl class="facts">
          <div><dt>Mode</dt><dd>{{ variant.gameMode }}</dd></div>
          <div v-if="lengthLabel(variant.trackLengthM)"><dt>Lap length</dt><dd>{{ lengthLabel(variant.trackLengthM) }}</dd></div>
          <div v-if="detail"><dt>Races here</dt><dd>{{ detail.raceCount }}</dd></div>
          <div v-if="detail?.typicalLapMs">
            <dt>Typical lap</dt>
            <dd :title="`The middle of ${count(detail.typicalLapFinishes, 'finish', 'finishes')} here, start and traffic included`">
              {{ formatRaceTime(detail.typicalLapMs) }}
            </dd>
          </div>
        </dl>

        <div v-if="detail?.typicalLapMs" class="estimate">
          <label for="estimate-laps">Race of</label>
          <NInputNumber
            id="estimate-laps"
            v-model:value="laps"
            :min="1"
            :max="99"
            size="medium"
            class="laps"
            :input-props="{ 'aria-label': 'Laps' }"
          />
          <span>laps: <strong>{{ estimate || '–' }}</strong></span>
        </div>

        <NAlert v-if="detailError" type="warning" :title="detailError" class="notice">
          <NButton size="medium" @click="loadDetail(variant.variantId)">Try again</NButton>
        </NAlert>
        <NSkeleton v-else-if="loadingDetail" text :repeat="4" />

        <template v-else-if="detail">
          <div class="records">
            <section aria-labelledby="fastest-title">
              <h3 id="fastest-title">Fastest laps</h3>
              <ol v-if="detail.fastestLaps.length" class="record-list">
                <li v-for="(record, index) in detail.fastestLaps" :key="index">
                  <span class="place">{{ index + 1 }}</span>
                  <span class="who"><strong>{{ record.name }}</strong><small>{{ record.vehicleName }} · {{ formatWhen(record.endedAt) }}</small></span>
                  <span class="value">{{ formatRaceTime(record.lapMs) }}</span>
                </li>
              </ol>
              <p v-else class="none">No laps logged here yet.</p>
            </section>
            <section aria-labelledby="wins-title">
              <h3 id="wins-title">Most wins</h3>
              <ol v-if="detail.mostWins.length" class="record-list">
                <li v-for="(record, index) in detail.mostWins" :key="index">
                  <span class="place">{{ index + 1 }}</span>
                  <span class="who"><strong>{{ record.name }}</strong><small>{{ count(record.races, 'race', 'races') }} here</small></span>
                  <span class="value">{{ count(record.wins, 'win', 'wins') }}</span>
                </li>
              </ol>
              <p v-else class="none">Nobody has won here yet.</p>
            </section>
          </div>

          <section v-if="detail.cups.length" aria-labelledby="cups-title" class="cups">
            <h3 id="cups-title">In cups</h3>
            <ul>
              <li v-for="cup in detail.cups" :key="cup.name">
                <strong>{{ cup.name }}</strong>
                <StatusBadge v-if="cup.isActive" tone="positive" compact>ACTIVE</StatusBadge>
                <small v-if="cup.nextOccurrence">Next {{ formatWhen(cup.nextOccurrence) }}<template v-if="cup.repeat"> · {{ cup.repeat }}</template></small>
              </li>
            </ul>
          </section>

          <section aria-labelledby="races-title">
            <h3 id="races-title">Latest races</h3>
            <div v-if="detail.recentRaces.length" class="races">
              <RaceCard v-for="race in detail.recentRaces" :key="race.id" :race="race" :now="now" />
            </div>
            <p v-else class="none">No races logged on this layout yet.</p>
          </section>
        </template>
      </section>
    </template>
  </section>
</template>

<style scoped>
.spectator { color: var(--page-text); font-size: var(--font-body); line-height: var(--line-reading); }
.back { display: inline-block; margin-bottom: 24px; color: var(--text-secondary); font-size: var(--font-support); text-decoration: none; }
.back:hover { color: var(--accent); }
.back:focus-visible, .layouts a:focus-visible, .workshop:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.hero-skeleton { height: 360px; border-radius: 16px; }
.missing { margin: 48px 0; }
.hero { display: grid; grid-template-columns: minmax(0, 1.6fr) minmax(0, 1fr); gap: 36px; align-items: end; }
.picture { position: relative; aspect-ratio: 832 / 386; border-radius: 16px; overflow: hidden; background: #2b2f36; }
.picture img { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; }
/* The game's outline is white on clear: shown whole, over the picture. */
.picture .layout { object-fit: contain; filter: drop-shadow(0 0 2px rgba(0, 0, 0, .8)); }
.placeholder { display: grid; place-items: center; height: 100%; font-size: var(--font-display); font-weight: 700; color: #8a919c; }
.eyebrow { margin: 0; font-size: var(--font-label); font-weight: 650; letter-spacing: .06em; text-transform: uppercase; color: var(--text-secondary); }
h1 { margin: 12px 0 0; font-size: var(--font-display); line-height: 1.15; letter-spacing: -.03em; overflow-wrap: anywhere; }
.workshop { display: inline-block; margin-top: 16px; color: var(--accent); font-size: var(--font-support); }
.layouts { display: flex; flex-wrap: wrap; gap: 8px; margin: 36px 0 32px; padding-bottom: 24px; border-bottom: 1px solid var(--border-color); }
.layouts a { padding: 8px 16px; border-radius: 999px; background: var(--surface); color: var(--page-text); font-size: var(--font-control); text-decoration: none; }
.layouts a:hover { color: var(--accent); }
.layouts a.current { background: var(--accent); color: var(--surface); font-weight: 600; }
.variant-head { display: flex; align-items: baseline; flex-wrap: wrap; gap: 8px 16px; }
h2 { margin: 0; font-size: var(--font-section); line-height: 1.3; }
.variant-id { min-width: 0; color: var(--text-muted); font-size: var(--font-meta); overflow-wrap: anywhere; }
.facts { display: flex; flex-wrap: wrap; gap: 16px 48px; margin: 24px 0; }
.facts div { min-width: 0; }
.facts dt { font-size: var(--font-label); color: var(--text-secondary); }
.facts dd { margin: 4px 0 0; font-size: var(--font-item); font-weight: 600; font-variant-numeric: tabular-nums; }
.estimate { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin-bottom: 36px; font-size: var(--font-support); }
.laps { width: 110px; }
.notice { margin: 24px 0; }
.records { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 48px; margin-bottom: 40px; }
h3 { margin: 0 0 16px; font-size: var(--font-event); line-height: 1.3; }
.record-list { list-style: none; margin: 0; padding: 0; }
.record-list li { display: flex; align-items: center; gap: 16px; padding: 14px 0; border-top: 1px solid var(--border-color); }
.place { display: grid; place-items: center; flex: 0 0 36px; height: 36px; border-radius: 50%; background: var(--surface); color: var(--accent); font-size: var(--font-support); font-weight: 700; }
.who { display: flex; flex-direction: column; min-width: 0; flex: 1; overflow-wrap: anywhere; }
.who strong { font-size: var(--font-roster); font-weight: 600; }
.who small { font-size: var(--font-support); color: var(--text-secondary); }
.value { font-size: var(--font-support); font-variant-numeric: tabular-nums; white-space: nowrap; }
.none { color: var(--text-secondary); font-size: var(--font-support); margin: 0; }
.cups { margin-bottom: 40px; }
.cups ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 12px; }
.cups li { display: flex; align-items: baseline; flex-wrap: wrap; gap: 8px 12px; padding: 16px 20px; border-radius: 12px; background: var(--surface); }
.cups strong { font-size: var(--font-roster); }
.cups small { flex-basis: 100%; color: var(--text-secondary); font-size: var(--font-support); }
.races { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 24px; }
.visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
@media (max-width: 760px) {
  .hero { grid-template-columns: minmax(0, 1fr); gap: 24px; }
  .records { grid-template-columns: minmax(0, 1fr); gap: 36px; }
  .races { grid-template-columns: minmax(0, 1fr); }
}
</style>
