<script setup lang="ts" generic="Draft extends object">
import { computed } from 'vue'
import { NAlert, NButton, NSpace, NTable } from 'naive-ui'

/**
 * Shown when a save found the row changed elsewhere since it was opened: which fields
 * differ, and a choice. "Use theirs" drops my edit; "Keep mine" keeps my values on top of
 * their version, to save again. Rendered inside the editor's dialog, in place of the form.
 */
const props = defineProps<{
  mine: Draft
  theirs: Draft
  /** Field labels, for the fields to show. */
  labels: Partial<Record<keyof Draft, string>>
}>()

const emit = defineEmits<{ theirs: []; mine: [] }>()

function show(value: unknown): string {
  const text = Array.isArray(value) ? value.join(', ') : value === null || value === undefined ? '' : String(value)
  return text === '' ? '(empty)' : text
}

const differences = computed(() =>
  (Object.keys(props.labels) as (keyof Draft)[])
    .filter((key) => JSON.stringify(props.mine[key]) !== JSON.stringify(props.theirs[key]))
    .map((key) => ({ key: String(key), label: props.labels[key]!, mine: show(props.mine[key]), theirs: show(props.theirs[key]) })),
)
</script>

<template>
  <div role="group" aria-label="Changed elsewhere">
    <NAlert type="warning" title="Changed elsewhere" class="gap">
      Someone else saved this while you were editing, so nothing of yours was saved yet.
    </NAlert>
    <NTable v-if="differences.length" size="small" class="gap">
      <thead>
        <tr>
          <th>Field</th>
          <th>Yours</th>
          <th>Saved meanwhile</th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="row in differences" :key="row.key">
          <td>{{ row.label }}</td>
          <td>{{ row.mine }}</td>
          <td>{{ row.theirs }}</td>
        </tr>
      </tbody>
    </NTable>
    <p v-else>Your values and the saved ones are the same.</p>
    <NSpace justify="end">
      <NButton @click="emit('theirs')">Use theirs</NButton>
      <NButton type="primary" @click="emit('mine')">Keep mine</NButton>
    </NSpace>
  </div>
</template>

<style scoped>
.gap {
  margin-bottom: 12px;
}
</style>
