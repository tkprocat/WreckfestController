<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NAlert, NButton, NCard, NCheckbox, NForm, NFormItem, NInput } from 'naive-ui'
import { restartHub } from '@/realtime/hub'
import { useAuthStore, type LoginFailure } from '@/stores/auth'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

const login = ref('')
const password = ref('')
const remember = ref(false)
const busy = ref(false)
const failure = ref<LoginFailure | null>(null)

const messages: Record<LoginFailure, string> = {
  invalid: 'That login or password is not right.',
  locked: 'This account is locked for now. Try again later, or ask another admin.',
  'rate-limited': 'Too many attempts. Wait a minute and try again.',
  unavailable: 'Signing in is not possible right now.',
}

/** Only paths inside the app, so a crafted link cannot send the user elsewhere. */
function redirectTarget(): string {
  const redirect = route.query.redirect
  return typeof redirect === 'string' && redirect.startsWith('/') && !redirect.startsWith('//') ? redirect : '/'
}

async function submit() {
  busy.value = true
  failure.value = null
  try {
    failure.value = await auth.login(login.value, password.value, remember.value)
    if (failure.value === null) {
      await restartHub().catch(() => undefined)
      await router.replace(redirectTarget())
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <NCard title="Sign in" class="login-card">
    <NAlert v-if="auth.degraded" type="error" title="The controller's database is unavailable">
      Sign-in is not possible until it is fixed. See the controller window for details.
    </NAlert>
    <NAlert v-else-if="auth.setupRequired" type="info" title="No accounts yet">
      Create the first admin account in the controller window.
    </NAlert>
    <NForm v-else @submit.prevent="submit">
      <NFormItem label="Username or email">
        <NInput v-model:value="login" autocomplete="username" :input-props="{ name: 'login' }" />
      </NFormItem>
      <NFormItem label="Password">
        <NInput
          v-model:value="password"
          type="password"
          show-password-on="click"
          autocomplete="current-password"
          :input-props="{ name: 'password' }"
        />
      </NFormItem>
      <NCheckbox v-model:checked="remember">Keep me signed in</NCheckbox>
      <NAlert v-if="failure" type="warning" class="login-failure">{{ messages[failure] }}</NAlert>
      <NButton type="primary" attr-type="submit" block :loading="busy" :disabled="!login || !password" class="login-submit">
        Sign in
      </NButton>
    </NForm>
  </NCard>
</template>

<style scoped>
.login-card {
  max-width: 420px;
  margin: 48px auto;
}

.login-failure,
.login-submit {
  margin-top: 16px;
}
</style>
