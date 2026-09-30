<script setup lang="ts">
import { NCard } from 'naive-ui'
import RotationPanel from './rotation/RotationPanel.vue'
</script>

<template>
  <section>
    <h1>Rotation</h1>
    <NCard title="Now running">
      <p class="muted">The tracks in server_config.cfg: what the server runs from its next start.</p>
      <RotationPanel />
    </NCard>
  </section>
</template>
