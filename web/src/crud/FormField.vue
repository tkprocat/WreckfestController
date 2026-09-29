<script setup lang="ts">
import { computed } from 'vue'
import { NFormItem } from 'naive-ui'

/**
 * One form field: its label, the server's message for it (or a hint), and the error state.
 * The control gets its accessible name through the slot: a form item's visible label is
 * not tied to its control, so bind `input-props` (inputs, and filterable selects, whose
 * focus lands on an input) or `aria-label` (switches) from it.
 *
 *   <FormField label="Name" field="name" :errors="errors" v-slot="{ inputProps }">
 *     <NInput v-model:value="draft.name" :input-props="inputProps" />
 *   </FormField>
 */
const props = defineProps<{
  label: string
  /** The field's name as the request spells it, to find its message in `errors`. */
  field: string
  errors: Record<string, string>
  help?: string
  /** Shown instead of the help, as a warning: why the field cannot be changed. */
  unavailable?: string | null
}>()

const message = computed(() => props.errors[props.field] ?? props.unavailable ?? props.help)
const status = computed(() => (props.errors[props.field] ? 'error' : props.unavailable ? 'warning' : undefined))
const inputProps = computed(() => ({ 'aria-label': props.label }))
</script>

<template>
  <NFormItem :label="label" :feedback="message" :validation-status="status">
    <slot :input-props="inputProps" :aria-label="label" />
  </NFormItem>
</template>
