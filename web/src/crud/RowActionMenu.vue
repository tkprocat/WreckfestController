<script setup lang="ts">
import { nextTick } from 'vue'
import { NButton, NDropdown, type DropdownOption } from 'naive-ui'

const props = defineProps<{ label: string; actionId: string; options: DropdownOption[]; disabled?: boolean }>()
const emit = defineEmits<{ select: [key: string] }>()

async function select(key: string | number) {
  emit('select', String(key))
  await nextTick()
  // Naive UI removes the teleported menu after select; restore focus after it closes.
  window.setTimeout(() => {
    if (document.activeElement !== document.body) return
    const trigger = [...document.querySelectorAll<HTMLButtonElement>('button[data-row-action]')].find((button) => button.dataset.rowAction === props.actionId)
    const focusTarget = trigger ?? document.querySelector<HTMLElement>('.resource-toolbar input')
    focusTarget?.focus()
  }, 50)
}
</script>

<template>
  <NDropdown trigger="click" :options="options" @select="select">
    <NButton size="small" :disabled="disabled" :aria-label="'More actions for ' + label" :data-row-action="actionId">More</NButton>
  </NDropdown>
</template>
