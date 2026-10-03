<script setup lang="ts">
import { NResult } from 'naive-ui'
</script>

<template>
  <NResult status="404" title="Page not found" description="There is nothing at this address.">
    <template #footer>
      <RouterLink class="home-link" to="/">Back to Home</RouterLink>
    </template>
  </NResult>
</template>

<style scoped>
.home-link {
  display: inline-flex;
  align-items: center;
  min-height: 36px;
  padding: 0 16px;
  border-radius: var(--radius-control);
  background: var(--accent);
  color: var(--surface);
  font-weight: 600;
  text-decoration: none;
}
.home-link:hover { opacity: .9; }
</style>
