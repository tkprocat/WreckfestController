<script setup lang="ts">
import { computed } from 'vue'
import { NAlert, NButton, NCard, NEmpty, NSkeleton, NTag } from 'naive-ui'
import { usePublicOverview } from '@/composables/usePublicOverview'
import { formatFromNow, formatWhen } from '@/utils/format'

const { overview, error, loading, reload } = usePublicOverview()
const title = computed(() => overview.value?.serverName || 'Wreckfest server')
// Parse only game color markers. Text stays escaped by Vue interpolation.
const titleParts = computed(() => {
  const parts: { text: string; code: string | null }[] = []
  let code: string | null = null
  for (const part of title.value.split(/(\^[1-9])/g)) {
    if (/^\^[1-9]$/.test(part)) code = part[1]!
    else if (part) parts.push({ text: part, code })
  }
  return parts
})
const occupancy = computed(() => {
  const people = overview.value?.players
  if (!people) return ''
  const count = (n: number, label: string) => n + ' ' + label + (n > 1 ? 's' : '')
  const capacity = overview.value?.maxPlayers
  return count(people.humans, 'player') + ', ' + count(people.bots, 'bot') +
    (capacity != null ? ' / ' + capacity + ' total' : '')
})
const players = computed(() =>
  [...(overview.value?.players.list ?? [])].sort(
    (a, b) => Number(a.isBot) - Number(b.isBot) || a.name.localeCompare(b.name),
  ),
)
const statusLabel = computed(() =>
  error.value ? 'NO CONNECTION' : overview.value?.status.isRunning ? 'UP' : 'DOWN',
)
</script>

<template>
  <section class="home spectator">
    <NAlert v-if="error" type="warning" :title="error" class="notice">
      <template v-if="overview">Showing the last confirmed overview; it may be out of date.</template>
      <NButton v-if="overview" size="medium" class="retry" @click="reload()">Try again</NButton>
    </NAlert>

    <section v-if="loading && !overview" class="loading-panel" aria-label="Loading server overview">
      <NSkeleton text style="width: 45%; height: 30px" />
      <NSkeleton text style="width: 72%; height: 56px; margin-top: 28px" />
      <NSkeleton text style="width: 35%; margin-top: 20px" />
      <p role="status">Loading server overview…</p>
    </section>

    <NCard v-else-if="!overview" class="unavailable">
      <h1>Overview unavailable</h1>
      <p>The controller has not provided a server snapshot yet.</p>
      <NButton type="primary" @click="reload()">Try again</NButton>
    </NCard>

    <template v-else>
      <section class="race-hero" aria-labelledby="server-title">
        <div class="hero-content">
          <div class="hero-topline">
            <span class="server-status" :class="error ? 'status-down' : overview.status.isRunning ? 'status-up' : 'status-down'" role="status">{{ statusLabel }}</span>
            <h1 id="server-title"><span v-for="(part, index) in titleParts" :key="index" :class="part.code ? 'game-color-' + part.code : undefined">{{ part.text }}</span></h1>
          </div>
          <div class="current-race">
            <p v-if="error" class="stale-label">Last known · connection unavailable</p>
            <h2 v-if="overview.status.isRunning && overview.currentTrack">{{ overview.currentTrack.name }}</h2>
            <h2 v-else-if="overview.status.isRunning">In lobby</h2>
            <h2 v-else>Server offline</h2>
            <p v-if="overview.activeCup" class="cup-name">
              {{ error ? 'Last known cup' : 'Active cup' }} · {{ overview.activeCup.name }}
            </p>
          </div>
        </div>
        <div class="hero-details">
          <p class="occupancy">{{ occupancy }}<span v-if="error"> · at last check</span></p>
        </div>
      </section>

      <div class="home-panels">
        <NCard class="panel players-panel">
          <div class="panel-heading">
            <div><p class="eyebrow">{{ error ? 'Last known roster' : 'On the server' }}</p><h2>Players</h2></div>
            <NTag size="small" :bordered="false">{{ overview.players.humans }} player{{ overview.players.humans === 1 ? '' : 's' }}</NTag>
          </div>
          <ul v-if="players.length" class="player-list">
            <li v-for="(player, index) in players" :key="index + '-' + player.name">
              <span class="player-avatar" aria-hidden="true">{{ player.name.charAt(0).toUpperCase() }}</span>
              <span class="player-name">{{ player.name }}</span>
              <NTag v-if="player.isBot" size="small" :bordered="false">Bot</NTag>
            </li>
          </ul>
          <NEmpty v-else :description="overview.status.isRunning ? 'No players connected yet' : 'No players connected'" />
        </NCard>

        <NCard class="panel rotation-panel">
          <div class="panel-heading">
            <div><p class="eyebrow">{{ error ? 'Last known rotation' : 'Next on track' }}</p><h2>Rotation</h2></div>
            <span v-if="overview.rotation.tracks.length" class="panel-count">{{ overview.rotation.tracks.length }} tracks</span>
          </div>
          <p v-if="overview.rotation.name" class="rotation-name">{{ overview.rotation.name }}</p>
          <ol v-if="overview.rotation.tracks.length" class="rotation-list">
            <li v-for="(track, index) in overview.rotation.tracks" :key="index + '-' + track.id">
              <span class="rotation-number">{{ String(index + 1).padStart(2, '0') }}</span>
              <span class="rotation-track">
                <strong>{{ track.name }}</strong>
                <small v-if="track.gameMode || track.laps != null">
                  <template v-if="track.gameMode">{{ track.gameMode }}</template>
                  <template v-if="track.gameMode && track.laps != null"> · </template>
                  <template v-if="track.laps != null">{{ track.laps }} laps</template>
                </small>
              </span>
            </li>
          </ol>
          <NEmpty v-else description="No rotation set" />
        </NCard>

        <NCard class="panel events-panel">
          <div class="panel-heading"><div><p class="eyebrow">{{ error ? 'Last known schedule' : 'Scheduled events' }}</p><h2>Coming up</h2></div></div>
          <ul v-if="overview.upcomingCups.length" class="cup-list">
            <li v-for="cup in overview.upcomingCups" :key="cup.name + '-' + cup.nextOccurrence">
              <strong>{{ cup.name }}</strong>
              <time :datetime="cup.nextOccurrence">{{ formatWhen(cup.nextOccurrence) }}</time>
              <small>{{ formatFromNow(cup.nextOccurrence) }}<template v-if="cup.repeat"> · {{ cup.repeat }}</template></small>
              <p v-if="cup.description">{{ cup.description }}</p>
            </li>
          </ul>
          <NEmpty v-else description="No cups scheduled" />
        </NCard>
      </div>
      <footer class="snapshot-time">Last confirmed <time :datetime="overview.updatedAt">{{ formatWhen(overview.updatedAt) }}</time></footer>
    </template>
  </section>
</template>

<style scoped>
.server-status { display: inline-flex; align-items: center; gap: 10px; border-radius: 8px; padding: 6px 16px; font-size: 18px; font-weight: 800; letter-spacing: .06em; }
.server-status::before { content: ''; width: 10px; height: 10px; border-radius: 50%; background: currentColor; }
.status-up { color: #fff; background: #18733b; }
.status-down { color: #fff; background: #be2535; }
.stale-label { margin: 0 0 12px; color: var(--text-secondary); font-size: 18px; font-weight: 600; }
.game-color-1 { color: #d63030; } .game-color-2 { color: #218739; }
.game-color-3 { color: #c56a00; } .game-color-4 { color: #2355cf; }
.game-color-5 { color: #16849b; } .game-color-6 { color: #a33cc1; }
.game-color-7 { color: #fff; text-shadow: 0 1px 2px #222, 0 0 2px #222; }
.game-color-8 { color: #767676; }
.game-color-9 { color: #000; text-shadow: 0 1px 2px #fff, 0 0 2px #fff; }
:global([data-theme='dark'] .game-color-1) { color: #ff7373; }
:global([data-theme='dark'] .game-color-2) { color: #70d78b; }
:global([data-theme='dark'] .game-color-3) { color: #ffaf50; }
:global([data-theme='dark'] .game-color-4) { color: #729bff; }
:global([data-theme='dark'] .game-color-5) { color: #69d3eb; }
:global([data-theme='dark'] .game-color-6) { color: #dc8cf4; }
:global([data-theme='dark'] .game-color-8) { color: #a5a5a5; }

.spectator { color: var(--page-text); font-size: 18px; line-height: 1.65; }
.notice { margin-bottom: 32px; }
.retry { margin-left: 16px; }
.eyebrow, .section-label { font-size: 15px; font-weight: 650; color: var(--text-secondary); margin: 0; }
.eyebrow { letter-spacing: .06em; text-transform: uppercase; }
.loading-panel, .unavailable { padding: 32px; }
.race-hero { border-bottom: 1px solid var(--border-color); padding-bottom: 36px; }
.hero-topline { display: flex; align-items: center; gap: 20px; margin-bottom: 36px; }
.server-status { flex-shrink: 0; }
.race-hero h1 { margin: 0; min-width: 0; font-size: clamp(24px, 3vw, 36px); font-weight: 600; line-height: 1.35; overflow-wrap: anywhere; }
.current-race { padding: 32px 36px; border-left: 5px solid var(--accent); background: var(--surface); border-radius: 0 16px 16px 0; }
.current-race h2 { font-size: clamp(36px, 5vw, 64px); line-height: 1.15; letter-spacing: -.035em; margin: 14px 0 0; overflow-wrap: anywhere; }
.cup-name { margin: 20px 0 0; color: var(--text-secondary); }
.hero-details { display: flex; align-items: baseline; flex-wrap: wrap; gap: 20px 40px; padding: 28px 0 0; }
.occupancy { margin: 0; font-size: 22px; font-weight: 600; overflow-wrap: anywhere; }
.metric { font-size: 36px; font-weight: 700; }
.metric small { font-size: 22px; color: var(--text-secondary); }
.detail-line { margin: 0; }
.snapshot-time { color: var(--text-secondary); font-size: 16px; margin-top: 40px; padding-top: 24px; border-top: 1px solid var(--border-color); }
.home-panels { display: grid; grid-template-columns: minmax(0, 1.65fr) minmax(0, 1fr); gap: 48px; margin-top: 44px; align-items: start; }
.panel { min-width: 0; background: transparent; border: 0; }
.panel :deep(.n-card__content) { padding: 0; }
.rotation-panel { grid-column: 1; grid-row: 1; }
.players-panel { grid-column: 2; grid-row: 1; }
.events-panel { grid-column: 1 / -1; border-top: 1px solid var(--border-color); padding-top: 36px; }
.panel-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 20px; margin-bottom: 24px; }
.panel-heading h2 { margin: 8px 0 0; font-size: 30px; line-height: 1.3; }
.panel-count, .rotation-name { font-size: 17px; color: var(--text-secondary); }
.rotation-name { margin: 0 0 24px; }
.player-list, .rotation-list, .cup-list { padding: 0; margin: 0; list-style: none; }
.rotation-list li { display: flex; gap: 24px; padding: 24px 0; border-top: 1px solid var(--border-color); }
.rotation-number { font-size: 20px; font-weight: 700; color: var(--accent); padding-top: 2px; }
.rotation-track { display: flex; flex-direction: column; gap: 10px; min-width: 0; overflow-wrap: anywhere; }
.rotation-track strong { font-size: 22px; font-weight: 600; line-height: 1.4; }
.rotation-track small, .cup-list small { font-size: 17px; color: var(--text-secondary); }
.player-list li { display: flex; align-items: center; gap: 16px; padding: 18px 0; border-top: 1px solid var(--border-color); }
.player-avatar { display: grid; place-items: center; flex: 0 0 40px; height: 40px; border-radius: 50%; background: var(--surface); color: var(--accent); font-size: 18px; font-weight: 700; }
.player-name { min-width: 0; flex: 1; font-size: 19px; overflow-wrap: anywhere; }
.cup-list { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 32px; }
.cup-list li { display: flex; flex-direction: column; gap: 12px; min-width: 0; overflow-wrap: anywhere; padding: 24px; background: var(--surface); border-radius: 12px; }
.cup-list strong { font-size: 24px; }
.cup-list time { font-size: 19px; }
.cup-list p { font-size: 18px; margin: 0; }
.spectator :deep(.n-empty__description) { font-size: 18px; color: var(--text-secondary); }
.spectator :deep(.n-tag) { font-size: 15px; }
@media (max-width: 760px) {
 .home-panels { grid-template-columns: 1fr; gap: 40px; margin-top: 36px; }
 .rotation-panel, .players-panel, .events-panel { grid-column: auto; grid-row: auto; }
 .rotation-panel { order: 1; } .players-panel { order: 2; } .events-panel { order: 3; }
 .current-race { padding: 24px; }
 .hero-details { gap: 18px 28px; }
 .cup-list { grid-template-columns: 1fr; }
 .hero-topline { flex-wrap: wrap; gap: 16px; margin-bottom: 28px; }
}
</style>
