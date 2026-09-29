<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  NButton,
  NCard,
  NInput,
  NInputGroup,
  NSpace,
  NTag,
  useDialog,
  useMessage,
} from 'naive-ui'
import { api } from '@/api/client'
import { problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'
import { onHub, type HubEvents } from '@/realtime/hub'

type Status = components['schemas']['ServerStatusResponse']

/** The server actions, and whether each needs a second thought. */
const actions = {
  start: { label: 'Start', path: '/api/server/start', confirm: null },
  stop: { label: 'Stop', path: '/api/server/stop', confirm: 'Stop the server? Players are disconnected.' },
  restart: { label: 'Restart', path: '/api/server/restart', confirm: 'Restart the server with /restart? Players are disconnected.' },
  inject: { label: 'Inject hook', path: '/api/server/inject', confirm: null },
  update: { label: 'Update', path: '/api/server/update', confirm: 'Update the server with SteamCMD? It must be stopped.' },
  forcestop: { label: 'Force stop', path: '/api/server/forcestop', confirm: 'Kill the server process? Use this only when Stop does not work.' },
  forcerestart: { label: 'Force restart', path: '/api/server/forcerestart', confirm: 'Kill the server process and start it again?' },
} as const

type Action = keyof typeof actions

const message = useMessage()
const dialog = useDialog()

const status = ref<Status | null>(null)
const busy = ref<Action | 'command' | null>(null)
const command = ref('')

/** The console: the log file's tail when the page opens, then live lines from the hub. */
const MAX_LINES = 1000
const lines = ref<string[]>([])
const consoleBox = ref<HTMLElement | null>(null)

async function loadStatus() {
  const { data } = await api.GET('/api/server/status')
  status.value = data ?? null
}

async function loadLogTail() {
  const { data, error } = await api.GET('/api/server/logfile', { params: { query: { lines: 200 } } })
  if (data) {
    lines.value = [...data.output]
    await scrollToEnd()
  } else {
    lines.value = [`(${problemMessage(error, 'The log file could not be read.')})`]
  }
}

async function append(newLines: string[]) {
  lines.value.push(...newLines)
  if (lines.value.length > MAX_LINES) {
    lines.value.splice(0, lines.value.length - MAX_LINES)
  }

  await scrollToEnd()
}

async function scrollToEnd() {
  await nextTick()
  consoleBox.value?.scrollTo({ top: consoleBox.value.scrollHeight })
}

async function run(action: Action) {
  busy.value = action
  try {
    const { data, error } = await api.POST(actions[action].path)
    if (data) {
      message.success(data.message)
    } else {
      message.error(problemMessage(error, `${actions[action].label} did not work.`))
    }
  } finally {
    busy.value = null
    void loadStatus()
  }
}

function ask(action: Action) {
  const question = actions[action].confirm
  if (!question) {
    void run(action)
    return
  }

  dialog.warning({
    title: actions[action].label,
    content: question,
    positiveText: actions[action].label,
    negativeText: 'Cancel',
    onPositiveClick: () => void run(action),
  })
}

async function send() {
  const text = command.value.trim()
  if (!text) {
    return
  }

  busy.value = 'command'
  try {
    const { data, error } = await api.POST('/api/server/command', { body: { command: text } })
    if (data) {
      command.value = ''
    } else {
      message.error(problemMessage(error, 'The command was not sent.'))
    }
  } finally {
    busy.value = null
  }
}

const statusEvents: (keyof HubEvents)[] = ['ServerStarted', 'ServerStopped', 'ServerRestarted', 'ServerAttached']
const stops: (() => void)[] = []

onMounted(() => {
  void loadStatus()
  void loadLogTail()
  stops.push(
    ...statusEvents.map((event) => onHub(event, () => void loadStatus())),
    onHub('ConsoleLog', ({ logs }) => void append(logs)),
  )
})

onBeforeUnmount(() => stops.forEach((stop) => stop()))
</script>

<template>
  <section>
    <h1>
      Server control
      <NTag :type="status?.isRunning ? 'success' : 'default'" round size="small">
        {{ status?.isRunning ? `Running (PID ${status.processId})` : 'Stopped' }}
      </NTag>
    </h1>

    <NCard title="Actions" class="gap">
      <NSpace>
        <NButton type="primary" :loading="busy === 'start'" :disabled="!!busy || status?.isRunning" @click="ask('start')">Start</NButton>
        <NButton :loading="busy === 'stop'" :disabled="!!busy || !status?.isRunning" @click="ask('stop')">Stop</NButton>
        <NButton :loading="busy === 'restart'" :disabled="!!busy || !status?.isRunning" @click="ask('restart')">Restart</NButton>
        <NButton :loading="busy === 'inject'" :disabled="!!busy || !status?.isRunning" @click="ask('inject')">Inject hook</NButton>
        <NButton :loading="busy === 'update'" :disabled="!!busy || status?.isRunning" @click="ask('update')">Update</NButton>
      </NSpace>
      <NSpace class="danger">
        <NButton type="error" ghost :loading="busy === 'forcestop'" :disabled="!!busy || !status?.isRunning" @click="ask('forcestop')">
          Force stop
        </NButton>
        <!-- Like Force stop, only for a running server: stopped, it would just start it,
             under a confirmation that talks about killing a process. -->
        <NButton
          type="error"
          ghost
          :loading="busy === 'forcerestart'"
          :disabled="!!busy || !status?.isRunning"
          @click="ask('forcerestart')"
        >
          Force restart
        </NButton>
      </NSpace>
    </NCard>

    <NCard title="Console">
      <pre ref="consoleBox" class="console">{{ lines.join('\n') }}</pre>
      <NInputGroup>
        <NInput
          v-model:value="command"
          placeholder="A console command, such as /message Hello everyone"
          :disabled="!status?.isRunning"
          @keyup.enter="send"
        />
        <NButton type="primary" :loading="busy === 'command'" :disabled="!status?.isRunning || !command.trim()" @click="send">
          Send
        </NButton>
      </NInputGroup>
    </NCard>
  </section>
</template>

<style scoped>
h1 {
  display: flex;
  align-items: center;
  gap: 12px;
  margin-top: 0;
}

.gap {
  margin-bottom: 16px;
}

.danger {
  margin-top: 12px;
}

.console {
  height: 360px;
  overflow: auto;
  margin: 0 0 12px;
  padding: 10px 12px;
  border-radius: 4px;
  background: #111317;
  color: #d5d8dc;
  font-size: 12px;
  line-height: 1.45;
  white-space: pre-wrap;
  word-break: break-word;
}
</style>
