<script setup lang="ts">
withDefaults(defineProps<{ tone?: 'positive' | 'negative' | 'warning' | 'neutral'; compact?: boolean; live?: boolean }>(), { tone: 'neutral', compact: false, live: false })
</script>

<template>
  <span class="status-badge" :class="['status-' + tone, { compact }]" :role="live ? 'status' : undefined"><slot /></span>
</template>

<style scoped>
.status-badge { display: inline-flex; align-items: center; gap: 10px; border-radius: var(--radius-control); padding: 6px 16px; font-size: var(--font-body); font-weight: 800; letter-spacing: .06em; max-width: 100%; overflow-wrap: anywhere; }
.status-badge::before { content: ''; flex: 0 0 10px; height: 10px; border-radius: 50%; background: currentColor; }
.status-positive { color: var(--status-text); background: var(--status-positive); }
.status-negative { color: var(--status-text); background: var(--status-negative); }
.status-warning { color: var(--status-text); background: var(--status-warning); }
.compact { padding: 4px 10px; gap: 8px; font-size: var(--font-label); font-weight: 650; letter-spacing: 0; }
.compact::before { flex-basis: 8px; height: 8px; }
.status-neutral { color: var(--text-secondary); background: var(--surface); border: 1px solid var(--border-color); }
</style>
