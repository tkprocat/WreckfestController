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
.page-header { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 16px; margin-bottom: 24px; }
.page-heading { min-width: 0; flex: 1 1 260px; }
.page-title-row { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; }
h1 { margin: 0; color: var(--page-text); font-size: clamp(1.5rem, 2.5vw, 1.9rem); font-weight: 700; line-height: 1.25; letter-spacing: -0.035em; overflow-wrap: anywhere; }
p { margin: 8px 0 0; max-width: 72ch; color: var(--text-muted); line-height: 1.6; }
.page-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; max-width: 100%; }
</style>
