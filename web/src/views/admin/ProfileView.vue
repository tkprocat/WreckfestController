<script setup lang="ts">
import { computed, reactive, ref, watchEffect } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { NAlert, NButton, NCard, NForm, NFormItem, NInput, NSelect, NSpace, useMessage } from 'naive-ui'
import { api } from '@/api/client'
import { fieldErrors, problemMessage } from '@/api/problems'
import { useAuthStore } from '@/stores/auth'
import { timeZoneOptions } from '@/utils/timeZones'

const NO_ANSWER = 'No answer from the controller, so it is not known whether this was saved. Reload before trying again.'

const auth = useAuthStore()
const route = useRoute()
const router = useRouter()

/**
 * The client's own 401 handling leaves /api/auth/* to the page (sign-in answers 401 for a
 * wrong password), so an ended session is caught here: forget the user and sign in again.
 */
function sessionEnded(status: number): boolean {
  if (status !== 401) {
    return false
  }

  auth.sessionEnded()
  void router.push({ name: 'login', query: { redirect: route.fullPath } })
  return true
}
const message = useMessage()
const zones = timeZoneOptions()

// The profile: email, display name and time zone. The user name is fixed here; another
// admin can change it on the Users page.
const profile = reactive({ email: '', displayName: '', timeZone: null as string | null })
const profileErrors = ref<Record<string, string>>({})
const savingProfile = ref(false)

watchEffect(() => {
  const user = auth.user
  if (user) {
    profile.email = user.email ?? ''
    profile.displayName = user.displayName ?? ''
    profile.timeZone = user.timeZone
  }
})

const profileChanged = computed(() => {
  const user = auth.user
  return (
    !!user &&
    (profile.email !== (user.email ?? '') ||
      profile.displayName !== (user.displayName ?? '') ||
      profile.timeZone !== user.timeZone)
  )
})

async function saveProfile() {
  if (!profileChanged.value || savingProfile.value) {
    return
  }

  savingProfile.value = true
  profileErrors.value = {}
  try {
    const { data, error, response } = await api.PUT('/api/auth/me', {
      body: { email: profile.email, displayName: profile.displayName || null, timeZone: profile.timeZone },
    })
    if (sessionEnded(response.status)) {
      return
    }

    if (data) {
      auth.user = data
      message.success('Profile saved.')
    } else {
      profileErrors.value = fieldErrors(error)
      message.error(problemMessage(error, 'The profile was not saved.'))
    }
  } catch {
    message.error(NO_ANSWER)
  } finally {
    savingProfile.value = false
  }
}

// The password: the current one, and the new one twice. Changing it signs out this
// account's other sessions.
const password = reactive({ current: '', next: '', again: '' })
const passwordErrors = ref<Record<string, string>>({})
const savingPassword = ref(false)
const mismatch = computed(() => password.again !== '' && password.next !== password.again)
const canChangePassword = computed(() => !!password.current && !!password.next && password.next === password.again)

async function changePassword() {
  if (!canChangePassword.value || savingPassword.value) {
    return
  }

  savingPassword.value = true
  passwordErrors.value = {}
  try {
    const { error, response } = await api.POST('/api/auth/me/password', {
      body: { currentPassword: password.current, newPassword: password.next },
    })
    if (sessionEnded(response.status)) {
      return
    }

    if (response.ok) {
      Object.assign(password, { current: '', next: '', again: '' })
      message.success('Password changed. Your other sessions are signed out.')
    } else {
      passwordErrors.value = fieldErrors(error)
      message.error(problemMessage(error, 'The password was not changed.'))
    }
  } catch {
    message.error('No answer from the controller, so it is not known whether the password changed.')
  } finally {
    savingPassword.value = false
  }
}

function status(errors: Record<string, string>, field: string) {
  return errors[field] ? ('error' as const) : undefined
}
</script>

<template>
  <section>
    <h1>Profile</h1>
    <NAlert v-if="!auth.user" type="info" title="This sign-in has no profile">
      An API key is not an account. Sign in with a user name to edit a profile.
    </NAlert>

    <template v-else>
      <NCard :title="`Account: ${auth.user.userName}`" class="gap">
        <NForm label-placement="left" label-width="auto" :disabled="savingProfile" @submit.prevent="saveProfile">
          <NFormItem label="Email" :feedback="profileErrors.email" :validation-status="status(profileErrors, 'email')">
            <NInput v-model:value="profile.email" :input-props="{ 'aria-label': 'Email', type: 'email', autocomplete: 'email' }" />
          </NFormItem>
          <NFormItem
            label="Display name"
            :feedback="profileErrors.displayName"
            :validation-status="status(profileErrors, 'displayName')"
          >
            <NInput v-model:value="profile.displayName" :input-props="{ 'aria-label': 'Display name' }" />
          </NFormItem>
          <NFormItem
            label="Time zone"
            :feedback="profileErrors.timeZone ?? 'Times on the site are shown in this zone.'"
            :validation-status="status(profileErrors, 'timeZone')"
          >
            <NSelect
              v-model:value="profile.timeZone"
              :options="zones"
              filterable
              clearable
              :input-props="{ 'aria-label': 'Time zone' }"
            />
          </NFormItem>
          <NButton type="primary" attr-type="submit" :loading="savingProfile" :disabled="!profileChanged || savingProfile">
            Save
          </NButton>
        </NForm>
      </NCard>

      <NCard title="Change password">
        <NForm label-placement="left" label-width="auto" :disabled="savingPassword" @submit.prevent="changePassword">
          <NFormItem
            label="Current password"
            :feedback="passwordErrors.currentPassword"
            :validation-status="status(passwordErrors, 'currentPassword')"
          >
            <NInput
              v-model:value="password.current"
              type="password"
              :input-props="{ 'aria-label': 'Current password', autocomplete: 'current-password' }"
            />
          </NFormItem>
          <NFormItem
            label="New password"
            :feedback="passwordErrors.newPassword"
            :validation-status="status(passwordErrors, 'newPassword')"
          >
            <NInput
              v-model:value="password.next"
              type="password"
              :input-props="{ 'aria-label': 'New password', autocomplete: 'new-password' }"
            />
          </NFormItem>
          <NFormItem
            label="New password again"
            :feedback="mismatch ? 'The two new passwords are not the same.' : undefined"
            :validation-status="mismatch ? 'error' : undefined"
          >
            <NInput
              v-model:value="password.again"
              type="password"
              :input-props="{ 'aria-label': 'New password again', autocomplete: 'new-password' }"
            />
          </NFormItem>
          <NSpace>
            <NButton type="primary" attr-type="submit" :loading="savingPassword" :disabled="!canChangePassword || savingPassword">
              Change password
            </NButton>
          </NSpace>
        </NForm>
      </NCard>
    </template>
  </section>
</template>

<style scoped>
h1 {
  margin-top: 0;
}

.gap {
  margin-bottom: 16px;
}
</style>
