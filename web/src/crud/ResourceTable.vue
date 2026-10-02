<script setup lang="ts" generic="T extends { id: string | number }">
import { computed, ref } from 'vue'
import { NAlert, NButton, NDataTable, NInput, NSpace, type DataTableColumns, type DataTableProps } from 'naive-ui'

/**
 * A resource page's table: search across chosen fields, the list's states (loading, a
 * failed load with a retry, empty), and 25 rows a page. Row actions are columns the page
 * adds.
 */
const props = defineProps<{
  rows: T[]
  columns: DataTableColumns<T>
  /** The fields search looks in. */
  searchFields: (keyof T)[]
  /** What the rows are, for messages: "tags". */
  what: string
  loading?: boolean
  /** Preserve readable columns in dense tables; scroll inside the table on narrow screens. */
  minTableWidth?: number
  error?: string | null
  /** For tables with an expand column: what its trigger shows (a button, for keyboard users). */
  renderExpandIcon?: DataTableProps['renderExpandIcon']
}>()

const emit = defineEmits<{ retry: [] }>()

const search = ref('')

const shown = computed(() => {
  const term = search.value.trim().toLowerCase()
  if (!term) {
    return props.rows
  }

  return props.rows.filter((row) =>
    props.searchFields.some((field) => String(row[field] ?? '').toLowerCase().includes(term)),
  )
})

const empty = computed(() => (search.value.trim() ? `No ${props.what} match "${search.value.trim()}".` : `No ${props.what} yet.`))
</script>

<template>
  <div>
    <NAlert v-if="error" type="warning" :title="error" class="gap">
      <NButton size="small" @click="emit('retry')">Try again</NButton>
    </NAlert>
    <NSpace justify="space-between" align="center" class="gap">
      <NInput
        v-model:value="search"
        :placeholder="`Search ${what}`"
        clearable
        :input-props="{ 'aria-label': `Search ${what}` }"
        class="search"
      />
    </NSpace>
    <NDataTable
      :columns="columns"
      :data="shown"
      :scroll-x="minTableWidth"
      :loading="loading"
      :render-expand-icon="renderExpandIcon"
      :row-key="(row: T) => row.id"
      :bordered="false"
      size="small"
      :pagination="shown.length > 25 ? { pageSize: 25 } : false"
    >
      <template #empty>{{ empty }}</template>
    </NDataTable>
  </div>
</template>

<style scoped>
.gap {
  margin-bottom: 12px;
}

.search {
  min-width: 260px;
}
</style>
