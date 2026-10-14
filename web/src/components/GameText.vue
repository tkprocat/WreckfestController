<script setup lang="ts">
import { computed } from 'vue'

// Text from the game, such as a server name, with its color markers (^1 to ^9) shown as
// colors. Only the markers are parsed: the text stays escaped by Vue interpolation.
const props = defineProps<{ text: string }>()

const parts = computed(() => {
  const result: { text: string; code: string | null }[] = []
  let code: string | null = null
  for (const part of props.text.split(/(\^[1-9])/g)) {
    if (/^\^[1-9]$/.test(part)) code = part[1]!
    else if (part) result.push({ text: part, code })
  }
  return result
})
</script>

<template>
  <span><span v-for="(part, index) in parts" :key="index" :class="part.code ? 'game-color-' + part.code : undefined">{{ part.text }}</span></span>
</template>

<style scoped>
.game-color-1 { color: #d63030; } .game-color-2 { color: #218739; }
.game-color-3 { color: #c56a00; } .game-color-4 { color: #2355cf; }
.game-color-5 { color: #16849b; } .game-color-6 { color: #a33cc1; }
.game-color-7 { color: #fff; text-shadow: 0 1px 2px #222, 0 0 2px #222; }
.game-color-8 { color: #767676; }
.game-color-9 { color: #000; text-shadow: 0 1px 2px #fff, 0 0 2px #fff; }
:global([data-theme='dark'] .game-color-1) { color: #ff7373; }
:global([data-theme='dark'] .game-color-2) { color: #70d78b; }
:global([data-theme='dark'] .game-color-3) { color: #ffaf50; }
:global([data-theme='dark'] .game-color-4) { color: #729bff; }
:global([data-theme='dark'] .game-color-5) { color: #69d3eb; }
:global([data-theme='dark'] .game-color-6) { color: #dc8cf4; }
:global([data-theme='dark'] .game-color-8) { color: #a5a5a5; }
</style>
