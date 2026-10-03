<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import { computed, h, onMounted, reactive, ref } from 'vue'
import {
  NAlert,
  NButton,
  NCard,
  NForm,
  NFormItem,
  NInput,
  NModal,
  NSelect,
  NSpace,
  NTag,
  useDialog,
  useMessage,
  type DataTableColumns,
} from 'naive-ui'
import { api } from '@/api/client'
import { fieldErrors, problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'
import { useAuthStore } from '@/stores/auth'
import { browserTimeZone, timeZoneOptions } from '@/utils/timeZones'
import { vSelectFocus } from '@/crud/selectFocus'
import ResourceTable from '@/crud/ResourceTable.vue'
import RowActionMenu from '@/crud/RowActionMenu.vue'
import { modalSize } from '@/crud/modal'

type User = components['schemas']['UserResponse']

const NO_ANSWER = 'No answer from the controller, so it is not known whether this happened. Reload before trying again.'

const auth = useAuthStore()
const message = useMessage()
const dialog = useDialog()
const zones = timeZoneOptions()

const users = ref<User[]>([])
const loadError = ref<string | null>(null)
/** Until the first load answers, adding waits: that answer would replace the new row. */
const loaded = ref(false)
/** The account an action is running for, so its buttons wait. */
const busy = ref<string | null>(null)

const self = (user: User) => user.id === auth.user?.id

async function load() {
  try {
    const { data, error } = await api.GET('/api/users')
    if (data) {
      users.value = data
      loadError.value = null
      loaded.value = true
    } else {
      loadError.value = problemMessage(error, 'The accounts could not be loaded.')
    }
  } catch {
    loadError.value = 'The accounts could not be loaded: no answer from the controller.'
  }
}

function replace(user: User) {
  users.value = users.value.some((u) => u.id === user.id)
    ? users.value.map((u) => (u.id === user.id ? user : u))
    : [...users.value, user]
}

// Add and edit share one form. A new account gets a temporary password, which its owner
// changes on their Profile page.
const editing = ref<User | 'new' | null>(null)
const form = reactive({ userName: '', email: '', displayName: '', timeZone: null as string | null, password: '' })
const formErrors = ref<Record<string, string>>({})
const saving = ref(false)
const isNew = computed(() => editing.value === 'new')

function openNew() {
  Object.assign(form, { userName: '', email: '', displayName: '', timeZone: browserTimeZone(), password: '' })
  formErrors.value = {}
  editing.value = 'new'
}

function openEdit(user: User) {
  Object.assign(form, {
    userName: user.userName,
    email: user.email ?? '',
    displayName: user.displayName ?? '',
    timeZone: user.timeZone,
    password: '',
  })
  formErrors.value = {}
  editing.value = user
}

async function saveForm() {
  if (saving.value || editing.value === null) {
    return
  }

  saving.value = true
  formErrors.value = {}
  const body = {
    userName: form.userName.trim(),
    email: form.email.trim(),
    displayName: form.displayName.trim() || null,
    timeZone: form.timeZone,
  }
  try {
    const result =
      editing.value === 'new'
        ? await api.POST('/api/users', { body: { ...body, password: form.password } })
        : await api.PUT('/api/users/{id}', { params: { path: { id: editing.value.id } }, body })
    if (result.data) {
      replace(result.data)
      if (self(result.data)) {
        auth.user = result.data
      }

      message.success(isNew.value ? `Account ${result.data.userName} added.` : `Account ${result.data.userName} saved.`)
      editing.value = null
    } else {
      formErrors.value = fieldErrors(result.error)
      message.error(problemMessage(result.error, 'The account was not saved.'))
    }
  } catch {
    message.error(NO_ANSWER)
  } finally {
    saving.value = false
  }
}

// A password reset: a new temporary password, set without the old one. The account's
// sessions end.
const resetting = ref<User | null>(null)
const newPassword = ref('')
const resetError = ref<string | undefined>()

function openReset(user: User) {
  resetting.value = user
  newPassword.value = ''
  resetError.value = undefined
}

async function saveReset() {
  const user = resetting.value
  if (!user || saving.value || !newPassword.value) {
    return
  }

  saving.value = true
  resetError.value = undefined
  try {
    const { error, response } = await api.POST('/api/users/{id}/password', {
      params: { path: { id: user.id } },
      body: { newPassword: newPassword.value },
    })
    if (response.ok) {
      message.success(`Password reset for ${user.userName}. Their sessions are signed out.`)
      resetting.value = null
    } else {
      resetError.value = fieldErrors(error).newPassword ?? problemMessage(error, 'The password was not reset.')
    }
  } catch {
    message.error(NO_ANSWER)
  } finally {
    saving.value = false
  }
}

async function setLocked(user: User, locked: boolean) {
  if (busy.value) {
    return
  }

  busy.value = user.id
  try {
    const path = locked ? '/api/users/{id}/lock' : '/api/users/{id}/unlock'
    const { data, error } = await api.POST(path, { params: { path: { id: user.id } } })
    if (data) {
      replace(data)
      message.success(locked ? `${user.userName} is locked and signed out.` : `${user.userName} is unlocked.`)
    } else {
      message.error(problemMessage(error, locked ? 'The account was not locked.' : 'The account was not unlocked.'))
    }
  } catch {
    message.error(NO_ANSWER)
  } finally {
    busy.value = null
  }
}

function askDelete(user: User) {
  dialog.warning({
    title: 'Delete account',
    content: `Delete ${user.userName}? They can no longer sign in, and this cannot be undone.`,
    positiveText: 'Delete',
    negativeText: 'Cancel',
    onPositiveClick: () => void remove(user),
  })
}

async function remove(user: User) {
  if (busy.value) {
    return
  }

  busy.value = user.id
  try {
    const { error, response } = await api.DELETE('/api/users/{id}', { params: { path: { id: user.id } } })
    if (response.ok) {
      users.value = users.value.filter((u) => u.id !== user.id)
      message.success(`${user.userName} deleted.`)
    } else {
      message.error(problemMessage(error, 'The account was not deleted.'))
    }
  } catch {
    message.error(NO_ANSWER)
  } finally {
    busy.value = null
  }
}

const columns: DataTableColumns<User> = [
  {
    title: 'User name',
    key: 'userName',
    sorter: (a, b) => a.userName.localeCompare(b.userName),
    render: (user) => h('span', { class: 'row-name' }, [user.userName, self(user) ? h(NTag, { size: 'small', bordered: false, class: 'flag' }, { default: () => 'you' }) : null]),
  },
  { title: 'Display name', key: 'displayName', render: (user) => user.displayName ?? '' },
  { title: 'Email', key: 'email', render: (user) => user.email ?? '' },
  {
    title: 'Status',
    key: 'isLockedOut',
    width: 100,
    render: (user) => h(NTag, { type: user.isLockedOut ? 'error' : 'success', size: 'small' }, { default: () => (user.isLockedOut ? 'Locked' : 'Active') }),
  },
  {
    title: 'Actions',
    key: 'actions',
    width: 130,
    render: (user) =>
      h(NSpace, { size: 'small', wrap: false }, () => [
        h(NButton, { size: 'small', disabled: !!busy.value, 'aria-label': 'Edit ' + user.userName, onClick: () => openEdit(user) }, () => 'Edit'),
        h(RowActionMenu, {
          label: user.userName,
          actionId: 'user-' + user.id,
          disabled: !!busy.value,
          options: [
            { label: 'Reset password', key: 'password' },
            { label: user.isLockedOut ? 'Unlock' : 'Lock', key: 'lock', disabled: self(user) },
            { label: 'Delete', key: 'delete', disabled: self(user) },
          ],
          onSelect: (key: string) => {
            if (key === 'password') openReset(user)
            else if (!self(user) && key === 'lock') void setLocked(user, !user.isLockedOut)
            else if (!self(user) && key === 'delete') askDelete(user)
          },
        }),
      ]),
  },
]

function status(field: string) {
  return formErrors.value[field] ? ('error' as const) : undefined
}

onMounted(() => void load())
</script>

<template>
  <section>
    <PageHeader title="Users" description="Manage access to the controller.">
      <template #actions>
        <NButton type="primary" :disabled="!loaded || !!loadError" @click="openNew">Add account</NButton>
      </template>
    </PageHeader>
    <NAlert v-if="loadError" type="warning" :title="loadError" />
    <NCard v-else>
      <p class="muted">Every account is an admin. Nobody can lock or delete their own account.</p>
      <ResourceTable
        :rows="users"
        :columns="columns"
        :search-fields="['userName', 'displayName', 'email']"
        what="accounts"
        :loading="!loaded"
        :min-table-width="720"
      />
    </NCard>

    <NModal
      :show="editing !== null"
      preset="card"
      :title="isNew ? 'Add account' : 'Edit account'"
      :aria-label="isNew ? 'Add account' : 'Edit account'"
      v-bind="modalSize(520)"
      :mask-closable="!saving"
      @update:show="(show: boolean) => !show && !saving && (editing = null)"
    >
      <NForm label-placement="top" :disabled="saving" @submit.prevent="saveForm">
        <NFormItem label="User name" :feedback="formErrors.userName" :validation-status="status('userName')">
          <NInput v-model:value="form.userName" :input-props="{ 'aria-label': 'User name', autocomplete: 'off' }" />
        </NFormItem>
        <NFormItem label="Email" :feedback="formErrors.email" :validation-status="status('email')">
          <NInput v-model:value="form.email" :input-props="{ 'aria-label': 'Email', type: 'email', autocomplete: 'off' }" />
        </NFormItem>
        <NFormItem label="Display name" :feedback="formErrors.displayName" :validation-status="status('displayName')">
          <NInput v-model:value="form.displayName" :input-props="{ 'aria-label': 'Display name' }" />
        </NFormItem>
        <NFormItem label="Time zone" :feedback="formErrors.timeZone" :validation-status="status('timeZone')">
          <NSelect
            v-model:value="form.timeZone"
            :options="zones"
            filterable
            clearable
            v-select-focus="{ 'aria-label': 'Time zone' }"
            :input-props="{ 'aria-label': 'Time zone' }"
          />
        </NFormItem>
        <NFormItem
          v-if="isNew"
          label="Temporary password"
          :feedback="formErrors.password ?? 'Its owner changes it on their Profile page.'"
          :validation-status="status('password')"
        >
          <NInput
            v-model:value="form.password"
            type="password"
            :input-props="{ 'aria-label': 'Temporary password', autocomplete: 'new-password' }"
          />
        </NFormItem>
        <div class="form-actions">
          <NButton :disabled="saving" @click="editing = null">Cancel</NButton>
        <NButton type="primary" attr-type="submit" :loading="saving" :disabled="saving">
            {{ isNew ? 'Add' : 'Save' }}
          </NButton>
        </div>
      </NForm>
    </NModal>

    <NModal
      :show="resetting !== null"
      preset="card"
      :title="`Reset password: ${resetting?.userName ?? ''}`"
      :aria-label="`Reset password: ${resetting?.userName ?? ''}`"
      v-bind="modalSize(480)"
      :mask-closable="!saving"
      @update:show="(show: boolean) => !show && !saving && (resetting = null)"
    >
      <NForm :disabled="saving" @submit.prevent="saveReset">
        <NFormItem
          label="New temporary password"
          :feedback="resetError ?? 'Their sessions are signed out. They change it on their Profile page.'"
          :validation-status="resetError ? 'error' : undefined"
        >
          <NInput
            v-model:value="newPassword"
            type="password"
            :input-props="{ 'aria-label': 'New temporary password', autocomplete: 'new-password' }"
          />
        </NFormItem>
        <div class="form-actions">
          <NButton :disabled="saving" @click="resetting = null">Cancel</NButton>
        <NButton type="primary" attr-type="submit" :loading="saving" :disabled="saving || !newPassword">Reset password</NButton>
        </div>
      </NForm>
    </NModal>
  </section>
</template>

<style scoped>
.muted {
  color: var(--text-muted);
  margin-top: 0;
}
:deep(.flag) { margin-left: 6px; }
</style>
