<script setup lang="ts">
import { NTag } from 'naive-ui'
import RaceResultsTable from '@/components/RaceResultsTable.vue'
import TrackLink from '@/components/TrackLink.vue'
import type { PublicRace } from '@/composables/usePublicFeed'
import { formatFromNow, formatWhen } from '@/utils/format'
import { laps, podium, result, resultTitle } from '@/utils/raceResults'

const props = defineProps<{ race: PublicRace; now: Date }>()

function ago(): string {
  const text = formatFromNow(props.race.endedAt, props.now)
  return text === 'now' ? 'just now' : text
}
</script>

<template>
  <article class="race">
    <div class="race-heading">
      <h3><TrackLink :track="race.track" /></h3>
      <p class="race-meta">
        <time :datetime="race.endedAt" :title="formatWhen(race.endedAt)">{{ ago() }}</time>
        <template v-if="laps(race)"> · {{ laps(race) }}</template>
        <NTag v-if="race.cupName" size="small" :bordered="false" class="cup-tag">{{ race.cupName }}</NTag>
      </p>
    </div>

    <ol v-if="podium(race).length" class="podium" :aria-label="'Top finishers on ' + race.track.name">
      <li v-for="entry in podium(race)" :key="entry.position!" :class="{ bot: entry.isBot }">
        <span class="place">{{ entry.position }}</span>
        <span class="driver">
          <strong>{{ entry.name }}</strong>
          <NTag v-if="entry.isBot" size="small" :bordered="false">Bot</NTag>
          <small v-if="entry.vehicleName">{{ entry.vehicleName }}</small>
        </span>
        <span class="time" :title="resultTitle(entry)">{{ result(entry) }}</span>
      </li>
    </ol>
    <p v-else class="race-meta">Nobody was placed.</p>

    <details v-if="race.entries.length" class="full-results">
      <summary>Full results ({{ race.entries.length }} car{{ race.entries.length === 1 ? '' : 's' }})</summary>
      <RaceResultsTable :entries="race.entries" />
    </details>
  </article>
</template>

<style scoped>
.race { display: flex; flex-direction: column; gap: 20px; min-width: 0; height: 100%; box-sizing: border-box; padding: 24px; background: var(--surface); border-radius: 12px; }
.race-heading h3 { margin: 0; font-size: var(--font-event); line-height: 1.3; overflow-wrap: anywhere; }
.race-meta { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 8px 0 0; font-size: var(--font-support); color: var(--text-secondary); }
.cup-tag { margin-left: 4px; }
.podium { list-style: none; margin: 0; padding: 0; }
.podium li { display: flex; align-items: center; gap: 16px; padding: 12px 0; border-top: 1px solid var(--border-color); }
.place { display: grid; place-items: center; flex: 0 0 40px; height: 40px; border-radius: 50%; font-size: var(--font-count); font-weight: 700; color: var(--accent); background: var(--page-background); }
.driver { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 10px; min-width: 0; flex: 1; overflow-wrap: anywhere; }
.driver strong { font-size: var(--font-roster); font-weight: 600; }
.driver small { flex-basis: 100%; font-size: var(--font-support); color: var(--text-secondary); }
.time { font-size: var(--font-support); font-variant-numeric: tabular-nums; white-space: nowrap; }
/* Dimmed, not faded: muted text stays readable. */
.bot strong, .bot .place, .bot .time { color: var(--text-muted); font-weight: 400; }
.full-results summary { cursor: pointer; font-size: var(--font-support); font-weight: 600; color: var(--accent); padding: 4px 0; }
.full-results summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.race :deep(.n-tag) { font-size: var(--font-label); }
@media (max-width: 760px) { .race { padding: 20px; } }
</style>
