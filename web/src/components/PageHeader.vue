<script setup lang="ts">
defineProps<{ title: string; description?: string }>()
</script>

<template>
  <header class="page-header">
    <div class="page-heading">
      <div class="page-title-row">
        <!-- Focusable from script: mobile navigation lands keyboard users on the new page's heading. -->
        <h1 tabindex="-1">{{ title }}</h1>
        <slot name="status" />
      </div>
      <p v-if="description">{{ description }}</p>
    </div>
    <div v-if="$slots.actions" class="page-actions"><slot name="actions" /></div>
  </header>
</template>

<style scoped>
.page-header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: var(--space-6); margin-bottom: var(--space-9); }
.page-heading { min-width: 0; flex: 1 1 260px; }
.page-title-row { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; }
h1 { margin: 0; color: var(--page-text); font-size: var(--font-title); font-weight: 700; line-height: 1.35; letter-spacing: -0.035em; overflow-wrap: anywhere; }
p { margin: 8px 0 0; max-width: 72ch; color: var(--text-secondary); font-size: var(--font-body); line-height: var(--line-reading); }
.page-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; max-width: 100%; }
</style>
