<script setup lang="ts" generic="T extends { id: string | number }">
import { computed, ref } from 'vue'
import { NAlert, NButton, NDataTable, NInput, type DataTableColumns, type DataTableProps } from 'naive-ui'

/** Shared resource search, page filters, states and pagination. Pages supply their own row actions. */
const props = defineProps<{
  rows: T[]
  columns: DataTableColumns<T>
  searchFields: (keyof T)[]
  what: string
  loading?: boolean
  minTableWidth?: number
  error?: string | null
  /** Count before page-specific filters were applied. */
  totalRows?: number
  filtersActive?: boolean
  renderExpandIcon?: DataTableProps['renderExpandIcon']
}>()

const emit = defineEmits<{ retry: []; resetFilters: [] }>()
const search = ref('')

const shown = computed(() => {
  const term = search.value.trim().toLowerCase()
  return term
    ? props.rows.filter((row) =>
        props.searchFields.some((field) => String(row[field] ?? '').toLowerCase().includes(term)),
      )
    : props.rows
})
const hasFilters = computed(() => !!search.value.trim() || !!props.filtersActive)
const total = computed(() => props.totalRows ?? props.rows.length)
const empty = computed(() =>
  hasFilters.value ? 'No ' + props.what + ' match the current filters.' : 'No ' + props.what + ' yet.',
)

function resetFilters() {
  search.value = ''
  emit('resetFilters')
}
</script>

<template>
  <div>
    <NAlert v-if="error" type="warning" :title="error" class="gap">
      <NButton size="small" @click="emit('retry')">Try again</NButton>
    </NAlert>
    <div class="resource-toolbar">
      <NInput
        v-model:value="search"
        :placeholder="'Search ' + what"
        clearable
        :input-props="{ 'aria-label': 'Search ' + what }"
        class="search"
      />
      <div class="toolbar-status">
        <span role="status">{{ shown.length }}{{ hasFilters ? ' of ' + total : '' }} {{ what }}</span>
        <NButton v-if="hasFilters" size="small" @click="resetFilters">Clear filters</NButton>
      </div>
      <div v-if="$slots.filters" class="toolbar-filters"><slot name="filters" /></div>
    </div>
    <NDataTable
      :columns="columns"
      :data="shown"
      :scroll-x="minTableWidth"
      :loading="loading"
      :render-expand-icon="renderExpandIcon"
      :row-key="(row: T) => row.id"
      :bordered="false"
      size="medium"
      :pagination="shown.length > 25 ? { pageSize: 25 } : false"
    >
      <template #empty>{{ empty }}</template>
    </NDataTable>
  </div>
</template>

<style scoped>
.gap { margin-bottom: 12px; }
.resource-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 12px;
  margin-bottom: 14px;
}
.search {
  flex: 1 1 240px;
  min-width: 0;
  max-width: 440px;
}
.toolbar-status {
  display: flex;
  flex: 1 1 auto;
  align-items: center;
  justify-content: flex-end;
  flex-wrap: wrap;
  gap: 10px;
  color: var(--text-muted);
  white-space: nowrap;
}
.toolbar-filters { flex-basis: 100%; }
@media (max-width: 600px) {
  .search { flex-basis: 100%; max-width: none; }
  .toolbar-status { justify-content: space-between; }
}
</style>
