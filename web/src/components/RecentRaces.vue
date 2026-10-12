<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { NButton, NEmpty, NSkeleton } from 'naive-ui'
import ContentSection from '@/components/ContentSection.vue'
import CupRunCard from '@/components/CupRunCard.vue'
import RaceCard from '@/components/RaceCard.vue'
import { usePublicFeed, type PublicFeedItem } from '@/composables/usePublicFeed'

const { items, error, loading, reload } = usePublicFeed()

// "12 minutes ago" moves on while the page stays open.
const now = ref(new Date())
const clock = setInterval(() => (now.value = new Date()), 60_000)
onBeforeUnmount(() => clearInterval(clock))

const list = computed(() => items.value ?? [])

/** What the shorthand in the results means, when any of it is shown. */
const legend = computed(() => {
  const races = list.value.flatMap((item) => (item.race ? [item.race] : (item.cup?.races ?? [])))
  const outcomes = new Set(races.flatMap((race) => race.entries.map((e) => e.outcome)))
  return {
    estimated: outcomes.has('Projected'),
    dnf: outcomes.has('DidNotFinish'),
  }
})

function key(item: PublicFeedItem): string {
  return item.race ? 'race-' + item.race.id : 'cup-' + item.cup?.activatedAt
}
</script>

<template>
  <ContentSection class="recent-races" aria-labelledby="recent-races-title">
    <div class="panel-heading">
      <div>
        <p class="eyebrow">{{ error && items ? 'Last known results' : 'Results' }}</p>
        <h2 id="recent-races-title">Recent races</h2>
      </div>
    </div>

    <div v-if="loading && !items" aria-label="Loading recent races">
      <NSkeleton text :repeat="3" />
    </div>
    <div v-else-if="error" class="races-error">
      <p>{{ error }}<template v-if="items"> Showing the last list loaded.</template></p>
      <NButton size="small" @click="reload()">Try again</NButton>
    </div>

    <NEmpty v-if="items && !list.length" description="No races recorded yet" />
    <ol v-else-if="list.length" class="race-list">
      <li v-for="item in list" :key="key(item)" :class="{ wide: item.cup }">
        <CupRunCard v-if="item.cup" :cup="item.cup" :now="now" />
        <RaceCard v-else-if="item.race" :race="item.race" :now="now" />
      </li>
    </ol>
    <p v-if="legend.estimated || legend.dnf" class="legend">
      <span v-if="legend.estimated"><strong>est.</strong> – a bot's time, estimated by the game when the last player finished.</span>
      <span v-if="legend.dnf"><strong>DNF</strong> – did not finish.</span>
    </p>
  </ContentSection>
</template>

<style scoped>
.panel-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 20px; margin-bottom: 24px; }
.panel-heading h2 { margin: 8px 0 0; font-size: var(--font-section); line-height: 1.3; }
.eyebrow { font-size: var(--font-label); font-weight: 650; color: var(--text-secondary); margin: 0; letter-spacing: .06em; text-transform: uppercase; }
.races-error { display: flex; align-items: center; flex-wrap: wrap; gap: 12px 16px; margin-bottom: 24px; color: var(--text-secondary); }
.races-error p { margin: 0; }
.legend { display: flex; flex-wrap: wrap; gap: 4px 24px; margin: 24px 0 0; font-size: var(--font-meta); color: var(--text-secondary); }
.race-list { list-style: none; margin: 0; padding: 0; display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 32px; }
.race-list > li { min-width: 0; }
/* A cup run takes the whole row: its standings and races need the width. */
.race-list > li.wide { grid-column: 1 / -1; }
.recent-races :deep(.n-empty__description) { font-size: var(--font-body); color: var(--text-secondary); }
@media (max-width: 1100px) { .race-list { grid-template-columns: minmax(0, 1fr); } }
</style>
