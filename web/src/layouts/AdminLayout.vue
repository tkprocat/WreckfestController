<script setup lang="ts">
import { computed, h, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterLink, useRoute, useRouter } from 'vue-router'
import { NButton, NDrawer, NDrawerContent, NMenu, type MenuOption } from 'naive-ui'

const route = useRoute()
const router = useRouter()
const active = computed(() => String(route.name ?? ''))
const menuOpen = ref(false)
const menuButton = ref<InstanceType<typeof NButton> | null>(null)
const sidebar = ref<HTMLElement | null>(null)
const content = ref<HTMLElement | null>(null)

const groups = [
  { label: 'Server', pages: [
    { name: 'admin-dashboard', label: 'Dashboard', icon: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z' },
    { name: 'admin-server', label: 'Server control', icon: 'M12 3v9 M6 6a8 8 0 1 0 12 0' },
    { name: 'admin-config', label: 'Server config', icon: 'M4 6h16 M4 12h16 M4 18h16 M8 3v6 M16 9v6 M10 15v6' },
    { name: 'admin-settings', label: 'Settings', icon: 'M4 7h16 M4 17h16 M8 4v6 M16 14v6' },
  ]},
  { label: 'Content', pages: [
    { name: 'admin-tracks', label: 'Tracks', icon: 'M8 3h8a5 5 0 0 1 5 5v8a5 5 0 0 1-5 5H8a5 5 0 0 1-5-5V8a5 5 0 0 1 5-5Z M9 7h6a2 2 0 0 1 2 2v6a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2Z' },
    { name: 'admin-rotation', label: 'Rotation', icon: 'M20 8a8 8 0 0 0-14-3L3 8 M3 3v5h5 M4 16a8 8 0 0 0 14 3l3-3 M16 16h5v5' },
    { name: 'admin-collections', label: 'Collections', icon: 'M3 7h7l2 2h9v11H3Z M3 7V4h7l2 2h9v3' },
    { name: 'admin-cups', label: 'Cups', icon: 'M8 3h8v7a4 4 0 0 1-8 0Z M8 5H4v3a4 4 0 0 0 4 4 M16 5h4v3a4 4 0 0 1-4 4 M12 14v5 M8 21h8 M9 19h6' },
    { name: 'admin-tags', label: 'Tags', icon: 'M3 3h8l10 10-8 8L3 11Z M7 7h.01' },
  ]},
  { label: 'Administration', pages: [
    { name: 'admin-users', label: 'Users', icon: 'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 M9 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M17 4a4 4 0 0 1 0 7 M22 21v-2a4 4 0 0 0-3-4' },
    { name: 'admin-profile', label: 'Profile', icon: 'M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8 M4 21v-2a6 6 0 0 1 6-6h4a6 6 0 0 1 6 6v2' },
  ]},
]

// The drawer's links close it; the sidebar's are plain links.
const menuOptions = (inDrawer: boolean): MenuOption[] => groups.map(group => ({
  type: 'group', key: group.label, label: group.label,
  children: group.pages.map(page => ({
    key: page.name,
    label: () => h(RouterLink, {
      to: { name: page.name },
      onClick: inDrawer ? closeForNavigation : undefined,
    }, { default: () => page.label }),
    icon: () => h('svg', { viewBox: '0 0 24 24', width: 18, height: 18, fill: 'none', stroke: 'currentColor', 'stroke-width': 1.6, 'stroke-linecap': 'round', 'stroke-linejoin': 'round', 'aria-hidden': true }, [h('path', { d: page.icon })]),
  })),
}))
const sidebarMenu = menuOptions(false)
const drawerMenu = menuOptions(true)

// Closing on a desktop resize prevents a hidden mobile drawer locking body scroll.
let desktop: MediaQueryList | undefined
const closeOnDesktop = () => { if (desktop?.matches) menuOpen.value = false }
onMounted(() => {
  desktop = window.matchMedia('(min-width: 960px)')
  desktop.addEventListener('change', closeOnDesktop)
})
onBeforeUnmount(() => desktop?.removeEventListener('change', closeOnDesktop))

// Set when a drawer link is chosen: resolves once that navigation has finished (or failed).
let navigation: Promise<void> | undefined
function closeForNavigation(event: MouseEvent) {
  // RouterLink leaves modified and non-primary clicks (new tab or window) to the browser.
  if (event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return
  navigation = new Promise(resolve => { const stop = router.afterEach(() => { stop(); resolve() }) })
  menuOpen.value = false
}

/**
 * Where focus goes once the drawer has gone: the new page's heading after choosing a page,
 * the sidebar's current link when a resize to desktop closed it, otherwise the Menu button.
 */
async function afterDrawerClosed() {
  const pending = navigation
  navigation = undefined
  if (pending) {
    await pending
    await nextTick()
    const heading = content.value?.querySelector<HTMLElement>('h1')
    ;(heading ?? document.getElementById('main-content'))?.focus()
  } else if (desktop?.matches) {
    sidebar.value?.querySelector<HTMLElement>(`a[href="${router.resolve({ name: active.value }).href}"]`)?.focus()
  } else {
    menuButton.value?.$el.focus()
  }
}
</script>

<template>
  <div class="admin">
    <aside ref="sidebar" class="admin-sidebar">
      <div class="sidebar-heading">CONTROL ROOM</div>
      <nav aria-label="Administration"><NMenu :options="sidebarMenu" :value="active" :indent="20" /></nav>
      <RouterLink to="/" class="public-link">View public server page <span aria-hidden="true">↗</span></RouterLink>
    </aside>

    <div class="admin-workspace">
      <div class="mobile-navigation">
        <NButton ref="menuButton" aria-label="Open administration menu" :aria-expanded="menuOpen" aria-controls="admin-mobile-navigation" @click="menuOpen = true">
          <template #icon>
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M4 6h16M4 12h16M4 18h16" /></svg>
          </template>
          Menu
        </NButton>
        <span>Control room</span>
      </div>
      <div ref="content" class="admin-content"><RouterView /></div>
    </div>

    <NDrawer v-model:show="menuOpen" placement="left" width="min(300px, calc(100vw - 24px))" @after-leave="afterDrawerClosed">
      <NDrawerContent title="Control room" closable :body-content-style="{ padding: '8px 0' }">
        <nav id="admin-mobile-navigation" aria-label="Administration"><NMenu :options="drawerMenu" :value="active" :indent="20" /></nav>
        <RouterLink to="/" class="public-link" @click="menuOpen = false">View public server page</RouterLink>
      </NDrawerContent>
    </NDrawer>
  </div>
</template>

<style scoped>
.admin { display: grid; grid-template-columns: 228px minmax(0, 1fr); gap: 32px; min-height: calc(100vh - 140px); align-items: start; }
.admin-sidebar { position: sticky; top: 24px; max-height: calc(100vh - 48px); overflow-y: auto; background: var(--surface); border: 1px solid var(--header-border); border-radius: var(--radius-panel); padding: 20px 0 12px; }
.sidebar-heading { color: var(--text-muted); font-size: 11px; font-weight: 700; letter-spacing: 0.14em; padding: 0 24px 8px; }
.admin-workspace, .admin-content { min-width: 0; }
.public-link { display: block; margin: 12px 20px 0; padding: 16px 4px 4px; border-top: 1px solid var(--header-border); color: var(--text-muted); text-decoration: none; font-size: 12px; }
.public-link:hover { color: var(--accent); }
.mobile-navigation { display: none; }
.admin-content :deep(section) { min-width: 0; }
.admin-content :deep(.n-data-table) { max-width: 100%; overflow-x: auto; }
@media (max-width: 959px) {
  .admin { display: block; }
  .admin-sidebar { display: none; }
  .mobile-navigation { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 24px; }
  .mobile-navigation > span { color: var(--text-muted); font-size: 12px; font-weight: 600; letter-spacing: 0.06em; text-transform: uppercase; }
}
</style>
