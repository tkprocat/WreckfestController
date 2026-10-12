<script setup lang="ts">
import { NTag } from 'naive-ui'
import type { PublicRaceEntry } from '@/composables/usePublicFeed'
import { formatRaceTime } from '@/utils/format'
import { result, resultTitle } from '@/utils/raceResults'

defineProps<{ entries: PublicRaceEntry[] }>()
</script>

<template>
  <div class="table-scroll">
    <table>
      <thead>
        <tr><th scope="col">Pos</th><th scope="col">Driver</th><th scope="col">Car</th><th scope="col">Time</th><th scope="col">Best lap</th></tr>
      </thead>
      <tbody>
        <tr v-for="(entry, index) in entries" :key="index" :class="{ bot: entry.isBot }">
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
</template>

<style scoped>
.table-scroll { overflow-x: auto; margin-top: 12px; }
table { width: 100%; border-collapse: collapse; font-size: var(--font-control); }
th, td { text-align: left; padding: 10px 12px 10px 0; border-top: 1px solid var(--border-color); white-space: nowrap; }
thead th { font-size: var(--font-label); color: var(--text-secondary); font-weight: 650; border-top: none; }
tbody th { font-weight: 600; }
.num { font-variant-numeric: tabular-nums; text-align: right; }
thead th:first-child, thead th:nth-child(4), thead th:nth-child(5) { text-align: right; }
/* Dimmed, not faded: muted text stays readable. */
tr.bot th, tr.bot td { color: var(--text-muted); font-weight: 400; }
:deep(.n-tag) { font-size: var(--font-label); }
</style>
