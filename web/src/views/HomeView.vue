<script setup lang="ts">
import { computed } from 'vue'
import { NAlert, NButton, NCard, NEmpty, NSkeleton, NTag } from 'naive-ui'
import { usePublicOverview } from '@/composables/usePublicOverview'
import { formatFromNow, formatUptime, formatWhen } from '@/utils/format'

const { overview, error, loading, reload } = usePublicOverview()
const title = computed(() => overview.value?.serverName || 'Wreckfest server')
const players = computed(() =>
  [...(overview.value?.players.list ?? [])].sort(
    (a, b) => Number(a.isBot) - Number(b.isBot) || a.name.localeCompare(b.name),
  ),
)
const statusLabel = computed(() =>
  error.value ? 'Last known status' : overview.value?.status.isRunning ? 'Online' : 'Offline',
)
</script>

<template>
  <section class="home">
    <NAlert v-if="error" type="warning" :title="error" class="notice">
      <template v-if="overview">Showing the last confirmed overview; it may be out of date.</template>
      <NButton v-if="overview" size="small" class="retry" @click="reload()">Try again</NButton>
    </NAlert>

    <section v-if="loading && !overview" class="loading-panel" aria-label="Loading server overview">
      <p class="eyebrow">Server overview</p>
      <NSkeleton text style="width: 45%; height: 30px" />
      <NSkeleton text style="width: 72%; height: 56px; margin-top: 28px" />
      <NSkeleton text style="width: 35%; margin-top: 20px" />
      <p role="status">Loading server overview…</p>
    </section>

    <NCard v-else-if="!overview" class="unavailable">
      <p class="eyebrow">Server overview</p>
      <h1>Overview unavailable</h1>
      <p>The controller has not provided a server snapshot yet.</p>
      <NButton type="primary" @click="reload()">Try again</NButton>
    </NCard>

    <template v-else>
      <section class="race-hero" aria-labelledby="server-title">
        <div class="hero-content">
          <div class="hero-topline">
            <span class="eyebrow">{{ error ? 'Last confirmed overview' : 'Server overview' }}</span>
            <NTag :type="error ? 'warning' : overview.status.isRunning ? 'success' : 'default'" round>
              {{ statusLabel }}
            </NTag>
          </div>
          <h1 id="server-title">{{ title }}</h1>
          <div class="current-race">
            <p class="section-label">{{ error ? 'Last known track' : overview.status.isRunning ? 'Racing now' : 'Current state' }}</p>
            <h2 v-if="overview.status.isRunning && overview.currentTrack">{{ overview.currentTrack.name }}</h2>
            <h2 v-else-if="overview.status.isRunning">Between races</h2>
            <h2 v-else>Server offline</h2>
            <p v-if="overview.status.isRunning && overview.activeCup" class="cup-name">
              {{ error ? 'Last known cup' : 'Active cup' }} · {{ overview.activeCup.name }}
            </p>
          </div>
        </div>
        <div class="hero-details">
          <div class="occupancy">
            <span class="metric">{{ overview.players.humans }}<small v-if="overview.maxPlayers != null"> / {{ overview.maxPlayers }}</small></span>
            <span>human players{{ overview.maxPlayers != null ? ' / capacity' : '' }}</span>
          </div>
          <p v-if="overview.players.bots" class="detail-line">{{ overview.players.bots }} {{ overview.players.bots === 1 ? 'bot' : 'bots' }} on the server</p>
          <p v-if="overview.status.isRunning && overview.status.uptimeSeconds != null" class="detail-line">
            Up {{ formatUptime(overview.status.uptimeSeconds) }}
          </p>
          <p class="snapshot-time">
            Last confirmed <time :datetime="overview.updatedAt">{{ formatWhen(overview.updatedAt) }}</time>
            <span> · {{ formatFromNow(overview.updatedAt) }}</span>
          </p>
        </div>
      </section>

      <div class="home-panels">
        <NCard class="panel players-panel">
          <div class="panel-heading">
            <div><p class="eyebrow">{{ error ? 'Last known roster' : 'On the server' }}</p><h2>Players</h2></div>
            <NTag size="small" :bordered="false">{{ overview.players.humans }} human{{ overview.players.humans === 1 ? '' : 's' }}</NTag>
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

        <NCard class="panel">
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

        <NCard class="panel">
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
    </template>
  </section>
</template>

<style scoped>
.home { display: grid; gap: 20px; }
.eyebrow, .section-label {
  margin: 0;
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: .12em;
  text-transform: uppercase;
}
.notice { margin-bottom: 0; }
.retry { margin-left: 12px; }
.loading-panel, .race-hero {
  border: 1px solid var(--border-color);
  border-radius: var(--radius-panel);
  background: var(--surface);
}
.loading-panel { padding: clamp(24px, 5vw, 56px); }
.loading-panel p[role="status"] { color: var(--text-muted); }
.unavailable h1 { margin: 8px 0; }
.race-hero {
  position: relative;
  display: grid;
  grid-template-columns: minmax(0, 1.8fr) minmax(220px, .8fr);
  overflow: hidden;
  background: linear-gradient(115deg, color-mix(in srgb, var(--accent) 11%, var(--surface)), var(--surface) 58%);
}
.race-hero::before {
  content: '';
  position: absolute;
  inset: 0 auto 0 0;
  width: 5px;
  background: var(--accent);
}
.hero-content { padding: clamp(24px, 5vw, 52px); min-width: 0; }
.hero-topline { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; }
.race-hero h1 { margin: 16px 0 40px; font-size: clamp(25px, 3vw, 37px); line-height: 1.16; letter-spacing: -.035em; overflow-wrap: anywhere; }
.current-race h2 { margin: 8px 0 0; font-size: clamp(30px, 4vw, 52px); line-height: 1.08; letter-spacing: -.04em; overflow-wrap: anywhere; }
.cup-name { margin: 16px 0 0; color: var(--text-secondary); font-weight: 600; }
.hero-details {
  display: flex;
  flex-direction: column;
  justify-content: center;
  min-width: 0;
  padding: 32px;
  border-left: 1px solid var(--border-color);
  background: color-mix(in srgb, var(--surface) 82%, var(--accent));
}
.occupancy { display: flex; flex-direction: column; line-height: 1.2; }
.metric { font-size: 44px; font-weight: 750; letter-spacing: -.06em; }
.metric small { color: var(--text-muted); font-size: .52em; font-weight: 600; }
.detail-line { margin: 14px 0 0; color: var(--text-secondary); }
.snapshot-time { margin: 28px 0 0; padding-top: 16px; border-top: 1px solid var(--border-color); color: var(--text-muted); font-size: 12px; }
.home-panels { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); align-items: start; gap: 16px; }
.panel { min-width: 0; }
.panel-heading { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 16px; }
.panel-heading h2 { margin: 3px 0 0; font-size: 19px; line-height: 1.2; }
.panel-count { color: var(--text-muted); font-size: 12px; white-space: nowrap; }
.player-list, .rotation-list, .cup-list { margin: 0; padding: 0; list-style: none; max-height: 460px; overflow-y: auto; }
.player-list li { display: flex; align-items: center; gap: 10px; padding: 9px 0; border-top: 1px solid var(--border-color); }
.player-name, .rotation-track, .cup-list li { min-width: 0; overflow-wrap: anywhere; }
.player-avatar { display: grid; place-items: center; flex: 0 0 28px; width: 28px; height: 28px; border-radius: 50%; background: color-mix(in srgb, var(--accent) 15%, var(--surface)); color: var(--accent); font-weight: 700; }
.player-name { flex: 1; }
.rotation-name { margin: -7px 0 12px; color: var(--text-muted); font-size: 12px; overflow-wrap: anywhere; }
.rotation-list li { display: flex; gap: 12px; padding: 10px 0; border-top: 1px solid var(--border-color); }
.rotation-number { color: var(--accent); font-size: 12px; font-weight: 700; }
.rotation-track { display: flex; flex-direction: column; gap: 2px; }
.rotation-track small, .cup-list small { color: var(--text-muted); }
.cup-list li { display: flex; flex-direction: column; gap: 2px; padding: 12px 0; border-top: 1px solid var(--border-color); }
.cup-list time { font-weight: 600; }
.cup-list p { margin: 4px 0 0; color: var(--text-secondary); font-size: 12px; }
@media (max-width: 900px) {
  .home-panels { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .home-panels > :last-child { grid-column: 1 / -1; }
}
@media (max-width: 600px) {
  .race-hero { grid-template-columns: 1fr; }
  .hero-content { padding: 24px; }
  .race-hero h1 { margin: 12px 0 28px; }
  .hero-details { border-left: 0; border-top: 1px solid var(--border-color); padding: 20px 24px; }
  .metric { font-size: 34px; }
  .snapshot-time { margin-top: 18px; }
  .home-panels { grid-template-columns: 1fr; }
  .home-panels > :last-child { grid-column: auto; }
}
</style>
