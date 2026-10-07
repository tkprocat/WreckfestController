<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NAlert, NButton, NCard, NCheckbox, NForm, NFormItem, NInput } from 'naive-ui'
import { redirectTarget } from '@/router/redirect'
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

async function submit() {
  busy.value = true
  failure.value = null
  try {
    failure.value = await auth.login(login.value, password.value, remember.value).catch(() => 'unavailable' as const)
    if (failure.value === null) {
      await router.replace(redirectTarget(route.query.redirect))
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="login-page">
    <div class="login-intro">
      <span class="intro-mark" aria-hidden="true">W</span>
      <p class="eyebrow">Controller access</p>
      <h1>Take the controls.</h1>
      <p>Sign in to manage races, rotations, cups, and the people keeping the server running.</p>
    </div>
    <NCard class="login-card">
      <p class="eyebrow">Administration</p>
      <h2>Sign in</h2>
      <NAlert v-if="auth.unreachable && !auth.loaded" type="error" title="The controller cannot be reached">
        <p>Check that it is running, then try again.</p>
        <NButton size="small" @click="auth.load()">Try again</NButton>
      </NAlert>
      <NAlert v-else-if="auth.degraded" type="error" title="The controller's database is unavailable">
        Sign-in is not possible until it is fixed. See the controller window for details.
      </NAlert>
      <NAlert v-else-if="auth.setupRequired" type="info" title="No accounts yet">
        Create the first admin account in the controller window.
      </NAlert>
      <NForm v-else label-placement="top" :disabled="busy" @submit.prevent="submit">
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
        <NButton type="primary" attr-type="submit" block :loading="busy" :disabled="busy || !login || !password" class="login-submit">
          Sign in
        </NButton>
      </NForm>
    </NCard>
  </section>
</template>

<style scoped>
.login-page {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(320px, 420px);
  align-items: center;
  gap: clamp(28px, 6vw, 96px);
  max-width: 950px;
  margin: clamp(32px, 8vh, 96px) auto;
}
.login-intro { min-width: 0; }
.intro-mark {
  display: grid;
  place-items: center;
  width: 56px;
  height: 56px;
  border-radius: 15px;
  background: var(--accent);
  color: var(--surface);
  font-size: 32px;
  font-weight: 800;
  font-style: italic;
}
.eyebrow { margin: 0 0 8px; color: var(--text-secondary); font-size: var(--font-label); font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
.login-intro .eyebrow { margin-top: 28px; }
.login-intro h1 { margin: 0; font-size: var(--font-display); line-height: 1.05; letter-spacing: -.04em; }
.login-intro > p:last-child { max-width: 36ch; margin-top: 20px; color: var(--text-secondary); font-size: var(--font-body); }
.login-card { min-width: 0; }
.login-card h2 { margin: 0 0 24px; font-size: var(--font-section); letter-spacing: -.025em; }
.login-failure, .login-submit { margin-top: 16px; }
@media (max-width: 700px) {
  .login-page { grid-template-columns: 1fr; gap: 28px; margin: 28px auto; }
  .login-intro .eyebrow { margin-top: 18px; }
  .login-intro > p:last-child { margin: 10px 0 0; font-size: var(--font-meta); }
}
</style>
