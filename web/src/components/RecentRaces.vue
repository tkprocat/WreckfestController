<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { NEmpty, NSkeleton, NTag } from 'naive-ui'
import ContentSection from '@/components/ContentSection.vue'
import { usePublicRaces, type PublicRace, type PublicRaceEntry } from '@/composables/usePublicRaces'
import { formatFromNow, formatRaceTime, formatWhen } from '@/utils/format'

const { races, error, loading } = usePublicRaces()

// "12 minutes ago" moves on while the page stays open.
const now = ref(new Date())
const clock = setInterval(() => (now.value = new Date()), 60_000)
onBeforeUnmount(() => clearInterval(clock))

const list = computed(() => races.value ?? [])

function ago(race: PublicRace): string {
  const text = formatFromNow(race.endedAt, now.value)
  return text === 'now' ? 'just now' : text
}

/** The first three placed cars, bots included: they keep their real places. */
function podium(race: PublicRace): PublicRaceEntry[] {
  return race.entries.filter((e) => e.position != null).slice(0, 3)
}

function result(entry: PublicRaceEntry): string {
  if (entry.outcome === 'DidNotFinish') return 'DNF'
  const time = formatRaceTime(entry.timeMs)
  if (!time) return '–'
  return entry.outcome === 'Projected' ? '~' + time : time
}

function resultTitle(entry: PublicRaceEntry): string | undefined {
  if (entry.outcome === 'Projected') return "Estimated by the game: the bot hadn't finished when the race ended"
  if (entry.outcome === 'DidNotFinish') return 'Did not finish'
  return undefined
}
</script>

<template>
  <ContentSection class="recent-races" aria-labelledby="recent-races-title">
    <div class="panel-heading">
      <div>
        <p class="eyebrow">{{ error && races ? 'Last known results' : 'Results' }}</p>
        <h2 id="recent-races-title">Recent races</h2>
      </div>
    </div>

    <div v-if="loading && !races" aria-label="Loading recent races">
      <NSkeleton text :repeat="3" />
    </div>
    <p v-else-if="error && !races" class="races-error">{{ error }}</p>
    <NEmpty v-else-if="!list.length" description="No races recorded yet" />

    <ol v-else class="race-list">
      <li v-for="race in list" :key="race.id" class="race">
        <div class="race-heading">
          <h3>{{ race.track.name }}</h3>
          <p class="race-meta">
            <time :datetime="race.endedAt" :title="formatWhen(race.endedAt)">{{ ago(race) }}</time>
            <template v-if="race.laps > 0"> · {{ race.laps }} lap{{ race.laps === 1 ? '' : 's' }}</template>
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
          <div class="table-scroll">
            <table>
              <thead>
                <tr><th scope="col">Pos</th><th scope="col">Driver</th><th scope="col">Car</th><th scope="col">Time</th><th scope="col">Best lap</th></tr>
              </thead>
              <tbody>
                <tr v-for="(entry, index) in race.entries" :key="index" :class="{ bot: entry.isBot }">
                  <td class="num">{{ entry.position ?? '–' }}</td>
                  <th scope="row">
                    {{ entry.name }}
                    <NTag v-if="entry.isBot" size="small" :bordered="false">Bot</NTag>
                  </th>
                  <td>{{ entry.vehicleName || '–' }}</td>
                  <td class="num" :title="resultTitle(entry)">{{ result(entry) }}</td>
                  <td class="num">{{ formatRaceTime(entry.bestLapMs) || '–' }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </details>
      </li>
    </ol>
  </ContentSection>
</template>

<style scoped>
.panel-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 20px; margin-bottom: 24px; }
.panel-heading h2 { margin: 8px 0 0; font-size: var(--font-section); line-height: 1.3; }
.eyebrow { font-size: var(--font-label); font-weight: 650; color: var(--text-secondary); margin: 0; letter-spacing: .06em; text-transform: uppercase; }
.races-error { color: var(--text-secondary); margin: 0; }
.race-list, .podium { list-style: none; margin: 0; padding: 0; }
.race-list { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 32px; }
.race { display: flex; flex-direction: column; gap: 20px; min-width: 0; padding: 24px; background: var(--surface); border-radius: 12px; }
.race-heading h3 { margin: 0; font-size: var(--font-event); line-height: 1.3; overflow-wrap: anywhere; }
.race-meta { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin: 8px 0 0; font-size: var(--font-support); color: var(--text-secondary); }
.cup-tag { margin-left: 4px; }
.podium li { display: flex; align-items: center; gap: 16px; padding: 12px 0; border-top: 1px solid var(--border-color); }
.place { display: grid; place-items: center; flex: 0 0 40px; height: 40px; border-radius: 50%; font-size: var(--font-count); font-weight: 700; color: var(--accent); background: var(--page-background); }
.driver { display: flex; flex-wrap: wrap; align-items: baseline; gap: 4px 10px; min-width: 0; flex: 1; overflow-wrap: anywhere; }
.driver strong { font-size: var(--font-roster); font-weight: 600; }
.driver small { flex-basis: 100%; font-size: var(--font-support); color: var(--text-secondary); }
.time { font-size: var(--font-support); font-variant-numeric: tabular-nums; white-space: nowrap; }
/* Dimmed, not faded: muted text stays readable. */
.bot strong, .bot .place, .bot .time, tr.bot th, tr.bot td { color: var(--text-muted); font-weight: 400; }
.full-results summary { cursor: pointer; font-size: var(--font-support); font-weight: 600; color: var(--accent); padding: 4px 0; }
.full-results summary:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; border-radius: 4px; }
.table-scroll { overflow-x: auto; margin-top: 12px; }
table { width: 100%; border-collapse: collapse; font-size: var(--font-control); }
th, td { text-align: left; padding: 10px 12px 10px 0; border-top: 1px solid var(--border-color); white-space: nowrap; }
thead th { font-size: var(--font-label); color: var(--text-secondary); font-weight: 650; border-top: none; }
tbody th { font-weight: 600; }
.num { font-variant-numeric: tabular-nums; text-align: right; }
thead th:first-child, thead th:nth-child(4), thead th:nth-child(5) { text-align: right; }
.recent-races :deep(.n-empty__description) { font-size: var(--font-body); color: var(--text-secondary); }
.recent-races :deep(.n-tag) { font-size: var(--font-label); }
@media (max-width: 1100px) { .race-list { grid-template-columns: 1fr; } }
@media (max-width: 760px) { .race { padding: 20px; } }
</style>
