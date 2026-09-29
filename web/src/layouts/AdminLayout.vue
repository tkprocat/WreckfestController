<script setup lang="ts">
import { computed, h } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { NLayout, NLayoutContent, NLayoutSider, NMenu, type MenuOption } from 'naive-ui'

// The admin area's frame: a menu of its pages beside the page itself. Pages are added
// here as they are built.
const pages = [
  { name: 'admin-dashboard', label: 'Dashboard' },
  { name: 'admin-server', label: 'Server control' },
  { name: 'admin-settings', label: 'Settings' },
  { name: 'admin-users', label: 'Users' },
]

const menu: MenuOption[] = pages.map((page) => ({
  key: page.name,
  label: () => h(RouterLink, { to: { name: page.name } }, { default: () => page.label }),
}))

const route = useRoute()
const active = computed(() => String(route.name ?? ''))
</script>

<template>
  <NLayout has-sider class="admin">
    <!-- An arrow button collapses the menu: the "bar" trigger read as a stray scrollbar. -->
    <NLayoutSider
      bordered
      collapse-mode="width"
      :collapsed-width="0"
      :width="200"
      show-trigger="arrow-circle"
      :native-scrollbar="false"
    >
      <NMenu :options="menu" :value="active" />
    </NLayoutSider>
    <NLayoutContent class="admin-content">
      <RouterView />
    </NLayoutContent>
  </NLayout>
</template>

<style scoped>
.admin {
  min-height: calc(100vh - 120px);
  background: transparent;
}

.admin-content {
  padding: 0 0 0 24px;
  background: transparent;
}
</style>
