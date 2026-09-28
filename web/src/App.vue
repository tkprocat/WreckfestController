<script setup lang="ts">
import { computed } from 'vue'
import { darkTheme, NConfigProvider, NDialogProvider, NMessageProvider, useOsTheme } from 'naive-ui'

// Follow the operating system's light or dark setting.
const osTheme = useOsTheme()
const theme = computed(() => (osTheme.value === 'dark' ? darkTheme : null))
</script>

<template>
  <NConfigProvider :theme="theme">
    <NMessageProvider>
      <NDialogProvider>
        <header class="app-header">
          <RouterLink to="/" class="app-title">Wreckfest Controller</RouterLink>
          <RouterLink to="/login">Sign in</RouterLink>
        </header>
        <main class="app-main">
          <RouterView />
        </main>
      </NDialogProvider>
    </NMessageProvider>
  </NConfigProvider>
</template>
