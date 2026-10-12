<script setup lang="ts">
/**
 * A variant's name, linked to its public track page when it has one (#261): the API gives
 * `trackKey` only for a track the catalogue knows and shows. Otherwise plain text.
 */
defineProps<{ track: { id: string; name: string; trackKey: string | null } }>()
</script>

<template>
  <RouterLink
    v-if="track.trackKey"
    class="track-link"
    :to="{ name: 'track', params: { track: track.trackKey, variant: track.id } }"
  >{{ track.name }}</RouterLink>
  <template v-else>{{ track.name }}</template>
</template>

<style scoped>
.track-link {
  color: inherit;
  text-decoration: underline;
  text-decoration-color: color-mix(in srgb, currentColor 35%, transparent);
  text-underline-offset: 0.18em;
}
.track-link:hover {
  color: var(--accent);
  text-decoration-color: currentColor;
}
.track-link:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
  border-radius: 2px;
}
</style>
