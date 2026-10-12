<script setup lang="ts">
import { computed, onMounted, ref, shallowRef } from 'vue'
import { NAlert, NButton, NEmpty, NInput, NSkeleton } from 'naive-ui'
import { api } from '@/api/client'
import type { components } from '@/api/schema'
import { trackImageUrl } from '@/api/images'
import { originLabel } from './trackText'

/**
 * Every track that is not hidden, with its picture and its layouts (#261). Each links to
 * its page; a layout links straight to its tab there.
 */
type Track = components['schemas']['PublicTrackSummary']

const tracks = shallowRef<Track[] | null>(null)
const error = ref<string | null>(null)
const loading = ref(true)
const search = ref('')

async function load() {
  loading.value = true
  error.value = null
  try {
    const { data, response } = await api.GET('/api/public/tracks')
    if (data) tracks.value = data
    else error.value = response.status === 429 ? 'Too many requests. Wait a minute and try again.' : 'The tracks could not be loaded.'
  } catch {
    error.value = 'No answer from the server. Check your connection and try again.'
  } finally {
    loading.value = false
  }
}

/** Matches the track's name, its mod's, or a layout's name or id, ignoring case. */
const shown = computed(() => {
  const text = search.value.trim().toLowerCase()
  const all = tracks.value ?? []
  if (!text) return all
  return all.filter(
    (t) =>
      t.name.toLowerCase().includes(text) ||
      (t.modName ?? '').toLowerCase().includes(text) ||
      t.variants.some((v) => v.name.toLowerCase().includes(text) || v.variantId.toLowerCase().includes(text)),
  )
})

const count = computed(() => {
  const n = shown.value.length
  return `${n} track${n === 1 ? '' : 's'}`
})

onMounted(load)
</script>

<template>
  <section class="tracks spectator" aria-labelledby="tracks-title">
    <header class="page-head">
      <div>
        <p class="eyebrow">Track guide</p>
        <h1 id="tracks-title">Tracks</h1>
      </div>
      <NInput
        v-if="tracks && tracks.length > 0"
        v-model:value="search"
        clearable
        placeholder="Search tracks and layouts"
        class="search"
        :input-props="{ 'aria-label': 'Search tracks and layouts', type: 'search' }"
      />
    </header>

    <NAlert v-if="error" type="warning" :title="error" class="notice">
      <NButton size="medium" @click="load()">Try again</NButton>
    </NAlert>

    <div v-if="loading && !tracks" class="grid" aria-label="Loading tracks">
      <NSkeleton v-for="n in 6" :key="n" class="card skeleton" />
      <p role="status" class="visually-hidden">Loading tracks…</p>
    </div>

    <template v-else-if="tracks">
      <p class="count" role="status">{{ count }}</p>
      <ul v-if="shown.length" class="grid">
        <li v-for="track in shown" :key="track.key" class="card">
          <RouterLink :to="{ name: 'track', params: { track: track.key } }" class="picture" tabindex="-1" aria-hidden="true">
            <img v-if="track.images.small" :src="trackImageUrl(track.key, 'Small', track.images.small)" alt="" loading="lazy" />
            <span v-else class="placeholder">{{ track.name.charAt(0) }}</span>
          </RouterLink>
          <div class="body">
            <h2><RouterLink :to="{ name: 'track', params: { track: track.key } }">{{ track.name }}</RouterLink></h2>
            <p class="meta">{{ originLabel(track) }}</p>
            <ul class="layouts" :aria-label="`Layouts of ${track.name}`">
              <li v-for="variant in track.variants" :key="variant.variantId">
                <RouterLink :to="{ name: 'track', params: { track: track.key, variant: variant.variantId } }">{{ variant.name }}</RouterLink>
              </li>
            </ul>
          </div>
        </li>
      </ul>
      <NEmpty v-else :description="search ? 'No track matches that search.' : 'No tracks yet.'" />
    </template>
  </section>
</template>

<style scoped>
.spectator { color: var(--page-text); font-size: var(--font-body); line-height: var(--line-reading); }
.page-head { display: flex; align-items: flex-end; justify-content: space-between; flex-wrap: wrap; gap: 24px; margin-bottom: 24px; }
.eyebrow { margin: 0; font-size: var(--font-label); font-weight: 650; letter-spacing: .06em; text-transform: uppercase; color: var(--text-secondary); }
h1 { margin: 8px 0 0; font-size: var(--font-title); line-height: 1.25; }
.search { max-width: 360px; }
.notice { margin-bottom: 24px; }
.count { margin: 0 0 16px; color: var(--text-secondary); font-size: var(--font-support); }
.grid { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 32px; }
.card { display: flex; flex-direction: column; min-width: 0; background: var(--surface); border-radius: 12px; overflow: hidden; }
.skeleton { height: 300px; }
.picture { display: block; aspect-ratio: 250 / 116; background: var(--page-background); }
.picture img { display: block; width: 100%; height: 100%; object-fit: cover; }
.placeholder { display: grid; place-items: center; height: 100%; font-size: var(--font-display); font-weight: 700; color: var(--text-muted); }
.body { display: flex; flex-direction: column; gap: 8px; padding: 20px 24px 24px; min-width: 0; }
h2 { margin: 0; font-size: var(--font-event); line-height: 1.3; overflow-wrap: anywhere; }
h2 a { color: inherit; text-decoration: none; }
h2 a:hover { color: var(--accent); }
h2 a:focus-visible, .layouts a:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.meta { margin: 0; color: var(--text-secondary); font-size: var(--font-support); }
.layouts { list-style: none; display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0 0; padding: 0; }
.layouts a { display: inline-block; padding: 4px 12px; border-radius: 999px; background: var(--page-background); color: var(--page-text); font-size: var(--font-label); text-decoration: none; }
.layouts a:hover { color: var(--accent); }
.visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
@media (max-width: 760px) {
  .grid { gap: 20px; }
  .search { max-width: none; width: 100%; }
}
</style>
