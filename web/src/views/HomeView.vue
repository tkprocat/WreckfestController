<script setup lang="ts">
import { computed } from 'vue'
import { NAlert, NCard, NEmpty, NGi, NGrid, NSkeleton, NTag } from 'naive-ui'
import { usePublicOverview } from '@/composables/usePublicOverview'
import { formatFromNow, formatUptime, formatWhen } from '@/utils/format'

const { overview, error, loading } = usePublicOverview()

const title = computed(() => overview.value?.serverName || 'Wreckfest server')

const playerCount = computed(() => {
  const players = overview.value?.players
  if (!players) {
    return ''
  }

  const max = overview.value?.maxPlayers
  return max ? `${players.humans} / ${max}` : `${players.humans}`
})

/** Humans first, then bots, each alphabetically. */
const players = computed(() =>
  [...(overview.value?.players.list ?? [])].sort(
    (a, b) => Number(a.isBot) - Number(b.isBot) || a.name.localeCompare(b.name),
  ),
)
</script>

<template>
  <section class="home">
    <NAlert v-if="error && !overview" type="warning" :title="error" />

    <NSkeleton v-if="loading && !overview" text :repeat="6" />

    <template v-else-if="overview">
      <header class="home-header">
        <h1>{{ title }}</h1>
        <NTag :type="overview.status.isRunning ? 'success' : 'default'" round>
          {{ overview.status.isRunning ? 'Online' : 'Offline' }}
        </NTag>
        <span v-if="overview.status.isRunning && overview.status.uptimeSeconds != null" class="muted">
          up {{ formatUptime(overview.status.uptimeSeconds) }}
        </span>
      </header>

      <NGrid cols="1 m:2" responsive="screen" :x-gap="16" :y-gap="16">
        <NGi>
          <NCard title="Racing now">
            <p v-if="overview.currentTrack" class="now-racing">{{ overview.currentTrack.name }}</p>
            <NEmpty v-else :description="overview.status.isRunning ? 'Between races' : 'The server is offline'" />
            <p v-if="overview.activeCup" class="muted">Cup: {{ overview.activeCup.name }}</p>
          </NCard>
        </NGi>

        <NGi>
          <NCard :title="`Players ${playerCount}`">
            <ul v-if="players.length" class="plain-list">
              <li v-for="player in players" :key="player.name">
                {{ player.name }}
                <NTag v-if="player.isBot" size="small" :bordered="false">bot</NTag>
              </li>
            </ul>
            <NEmpty v-else description="Nobody is racing" />
          </NCard>
        </NGi>

        <NGi>
          <NCard :title="overview.rotation.name ? `Rotation: ${overview.rotation.name}` : 'Rotation'">
            <ol v-if="overview.rotation.tracks.length" class="rotation">
              <li v-for="(track, index) in overview.rotation.tracks" :key="`${index}-${track.id}`">
                <span>{{ track.name }}</span>
                <span class="muted">
                  <template v-if="track.gameMode">{{ track.gameMode }}</template>
                  <template v-if="track.gameMode && track.laps != null"> · </template>
                  <template v-if="track.laps != null">{{ track.laps }} laps</template>
                </span>
              </li>
            </ol>
            <NEmpty v-else description="No rotation set" />
          </NCard>
        </NGi>

        <NGi>
          <NCard title="Coming up">
            <ul v-if="overview.upcomingCups.length" class="plain-list cups">
              <li v-for="cup in overview.upcomingCups" :key="`${cup.name}-${cup.nextOccurrence}`">
                <strong>{{ cup.name }}</strong>
                <span class="muted">
                  {{ formatWhen(cup.nextOccurrence) }} ({{ formatFromNow(cup.nextOccurrence) }})
                  <template v-if="cup.repeat"> · {{ cup.repeat }}</template>
                </span>
                <span v-if="cup.description">{{ cup.description }}</span>
              </li>
            </ul>
            <NEmpty v-else description="No cups scheduled" />
          </NCard>
        </NGi>
      </NGrid>
    </template>
  </section>
</template>

<style scoped>
.home-header {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
  margin-bottom: 20px;
}

.home-header h1 {
  margin: 0;
  font-size: 1.6rem;
}

.now-racing {
  margin: 0 0 8px;
  font-size: 1.25rem;
  font-weight: 600;
}

.muted {
  opacity: 0.7;
}

.plain-list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.plain-list li {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
}

.cups li {
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  padding: 8px 0;
}

.rotation {
  margin: 0;
  padding-left: 1.4em;
}

.rotation li {
  padding: 3px 0;
}

.rotation li span + span {
  margin-left: 8px;
}
</style>
