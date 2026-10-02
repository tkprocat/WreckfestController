<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import { NCard } from 'naive-ui'
import RotationPanel from './rotation/RotationPanel.vue'
</script>

<template>
  <section>
    <PageHeader title="Rotation" description="Edit the configured track order for the next server start." />
    <NCard title="Configured rotation">
      <RotationPanel />
    </NCard>
  </section>
</template>
