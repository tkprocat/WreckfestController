<script setup lang="ts">
import { computed } from 'vue'
import { NTag } from 'naive-ui'
import StatusBadge from '@/components/StatusBadge.vue'
import RaceResultsTable from '@/components/RaceResultsTable.vue'
import type { PublicCupRun, PublicRace } from '@/composables/usePublicFeed'
import { formatFromNow, formatWhen } from '@/utils/format'
import { laps } from '@/utils/raceResults'

const props = defineProps<{ cup: PublicCupRun; now: Date }>()

const status = computed(() => {
  switch (props.cup.status) {
    case 'Warmup':
      return { label: 'WARMUP', tone: 'warning' as const }
    case 'Running':
      return { label: 'RUNNING', tone: 'positive' as const }
    default:
      return { label: 'FINAL', tone: 'neutral' as const }
  }
})

const when = computed(() => {
  const cup = props.cup
  if (cup.status === 'Warmup' && cup.startsAt) return 'Starts ' + formatWhen(cup.startsAt)
  if (cup.status === 'Running') {
    const started = 'Started ' + formatWhen(cup.startsAt ?? cup.activatedAt)
    return cup.endsAt ? started + ' · ends ' + formatWhen(cup.endsAt) : started
  }
  return formatWhen(cup.startsAt ?? cup.activatedAt)
})

const raceCount = computed(() => {
  const { raceCount: count, races } = props.cup
  const text = `${count} race${count === 1 ? '' : 's'}`
  return races.length < count ? `${text}, the latest ${races.length} listed` : text
})

/** Why there is no table: not started, nothing raced yet, or a cup without cup points. */
const noStandings = computed(() => {
  if (props.cup.status === 'Warmup') return 'The standings begin when the cup starts.'
  if (!props.cup.races.some((r) => !r.warmup)) return 'No races yet.'
  return 'No cup points were awarded.'
})

function ago(race: PublicRace): string {
  const text = formatFromNow(race.endedAt, props.now)
  return text === 'now' ? 'just now' : text
}

function winner(race: PublicRace): string | undefined {
  return race.entries.find((e) => e.position === 1)?.name
}
</script>

<template>
  <article class="cup" :aria-label="'Cup: ' + cup.name">
    <div class="cup-heading">
      <div class="cup-title">
        <p class="eyebrow">Cup</p>
        <h3>{{ cup.name }}</h3>
        <p class="cup-meta">
          <span>{{ when }}</span>
          <span>{{ raceCount }}</span>
        </p>
      </div>
      <StatusBadge compact :tone="status.tone">{{ status.label }}</StatusBadge>
    </div>

    <div class="cup-body">
      <section class="standings" :aria-label="'Standings, ' + cup.name">
        <h4>{{ cup.status === 'Ended' ? 'Final standings' : 'Standings' }}</h4>
        <ol v-if="cup.standings.length" class="standings-list">
          <li v-for="(row, index) in cup.standings" :key="index" :class="{ bot: row.isBot, leader: row.position === 1 }">
            <span class="place">{{ row.position }}</span>
            <span class="driver">
              <strong>{{ row.name }}</strong>
              <NTag v-if="row.isBot" size="small" :bordered="false">Bot</NTag>
            </span>
            <span class="points">{{ row.points }} <small>pts</small></span>
          </li>
        </ol>
        <p v-else class="cup-meta">{{ noStandings }}</p>
      </section>

      <section v-if="cup.races.length" class="cup-races" :aria-label="'Races, ' + cup.name">
        <h4>Races</h4>
        <ol class="cup-race-list">
          <li v-for="race in cup.races" :key="race.id">
            <details>
              <summary>
                <span class="race-name">{{ race.track.name }}</span>
                <span class="race-meta">
                  <time :datetime="race.endedAt" :title="formatWhen(race.endedAt)">{{ ago(race) }}</time>
                  <template v-if="laps(race)"> · {{ laps(race) }}</template>
                  <template v-if="winner(race)"> · won by {{ winner(race) }}</template>
                  <NTag v-if="race.warmup" size="small" :bordered="false" title="Ended in the warmup: it does not count toward the cup">Warmup</NTag>
                </span>
              </summary>
              <RaceResultsTable :entries="race.entries" />
            </details>
          </li>
        </ol>
      </section>
    </div>
  </article>
</template>

<style scoped>
.cup { display: flex; flex-direction: column; gap: 24px; min-width: 0; padding: 24px; background: var(--surface); border-radius: 12px; }
.cup-heading { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 12px 20px; }
.cup-title { min-width: 0; }
.eyebrow { font-size: var(--font-label); font-weight: 650; color: var(--text-secondary); margin: 0; letter-spacing: .06em; text-transform: uppercase; }
.cup-heading h3 { margin: 8px 0 0; font-size: var(--font-event); line-height: 1.3; overflow-wrap: anywhere; }
.cup-meta { display: flex; flex-wrap: wrap; gap: 4px 16px; margin: 8px 0 0; font-size: var(--font-support); color: var(--text-secondary); }
.cup-body { display: grid; grid-template-columns: minmax(0, 2fr) minmax(0, 3fr); gap: 32px; }
h4 { margin: 0 0 8px; font-size: var(--font-label); font-weight: 650; color: var(--text-secondary); letter-spacing: .06em; text-transform: uppercase; }
.standings-list, .cup-race-list { list-style: none; margin: 0; padding: 0; }
.standings-list li { display: flex; align-items: center; gap: 16px; padding: 10px 0; border-top: 1px solid var(--border-color); }
.place { display: grid; place-items: center; flex: 0 0 36px; height: 36px; border-radius: 50%; font-size: var(--font-support); font-weight: 700; color: var(--text-secondary); background: var(--page-background); font-variant-numeric: tabular-nums; }
.leader .place { color: var(--accent); }
.driver { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 10px; min-width: 0; flex: 1; overflow-wrap: anywhere; }
.driver strong { font-size: var(--font-roster); font-weight: 600; }
.points { font-size: var(--font-roster); font-weight: 700; font-variant-numeric: tabular-nums; white-space: nowrap; }
.points small { font-size: var(--font-label); font-weight: 600; color: var(--text-secondary); }
/* Dimmed, not faded: muted text stays readable. */
.bot strong, .bot .place, .bot .points { color: var(--text-muted); font-weight: 400; }
.cup-race-list li { border-top: 1px solid var(--border-color); }
summary { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 12px; cursor: pointer; padding: 12px 0; list-style-position: inside; }
summary::-webkit-details-marker { color: var(--accent); }
summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.race-name { font-size: var(--font-body); font-weight: 600; overflow-wrap: anywhere; }
.race-meta { display: inline-flex; align-items: center; flex-wrap: wrap; gap: 8px; min-width: 0; font-size: var(--font-support); color: var(--text-secondary); }
details[open] { padding-bottom: 12px; }
.cup :deep(.n-tag) { font-size: var(--font-label); }
@media (max-width: 1100px) { .cup-body { grid-template-columns: minmax(0, 1fr); } }
@media (max-width: 760px) { .cup { padding: 20px; } }
</style>
