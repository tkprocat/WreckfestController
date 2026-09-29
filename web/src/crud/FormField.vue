<script setup lang="ts">
import { computed, useId } from 'vue'
import { NFormItem } from 'naive-ui'

/**
 * One form field: its label, the server's message for it (or a hint), and the error state.
 * The control gets its accessible name through the slot: a form item's visible label is
 * not tied to its control, so bind `input-props` (inputs) or `control-props` (switches,
 * radio groups) from it. A select takes both `v-select-focus` and `input-props`: its tab
 * stop is a div, not its input (see selectFocus.ts). Both also tie the
 * message to the control, and mark the control invalid while the server refuses it.
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

const messageId = useId()
const invalid = computed(() => Boolean(props.errors[props.field]))
const message = computed(() => props.errors[props.field] ?? props.unavailable ?? props.help)
const status = computed(() => (invalid.value ? 'error' : props.unavailable ? 'warning' : undefined))
const inputProps = computed(() => ({
  'aria-label': props.label,
  'aria-describedby': message.value ? messageId : undefined,
  'aria-invalid': invalid.value ? ('true' as const) : undefined,
}))
</script>

<template>
  <NFormItem :label="label" :validation-status="status">
    <slot :input-props="inputProps" :control-props="inputProps" />
    <template v-if="message" #feedback>
      <span :id="messageId">{{ message }}</span>
    </template>
  </NFormItem>
</template>
