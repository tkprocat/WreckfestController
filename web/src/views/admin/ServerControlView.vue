<script setup lang="ts">
import PageHeader from '@/components/PageHeader.vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  NAlert,
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
import { useServerStatus } from '@/composables/useServerStatus'
import { onHub, type HubEvents } from '@/realtime/hub'
import { disruptiveCommand } from '@/utils/commands'

/** A request that got no answer: whether it happened is unknown until the status says. */
const NO_ANSWER = 'No answer from the controller, so it is not known whether this happened. Check the status before trying again.'

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

const { status, error: statusError, refreshing, load: loadStatus } = useServerStatus()

/**
 * Running, stopped, or unknown (null): not loaded, not loadable, or being refreshed. An
 * answer that arrives while a later load is on its way predates whatever made the page ask
 * again, so the actions wait for the latest one.
 */
const running = computed(() => (refreshing.value ? null : (status.value?.isRunning ?? null)))

/** One thing at a time: an action or a command in flight blocks the rest. */
const busy = ref<Action | 'command' | 'bot' | null>(null)
const command = ref('')

/** The console: the log file's tail when the page opens, then live lines from the hub. */
const MAX_LINES = 1000
const lines = ref<string[]>([])
const consoleBox = ref<HTMLElement | null>(null)

// Hub lines that arrive while the tail is loading go after it, not under it.
let tailLoaded = false
let pending: string[] = []

async function loadLogTail() {
  let head: string[]
  try {
    const { data, error } = await api.GET('/api/server/logfile', { params: { query: { lines: 200 } } })
    head = data ? [...data.output] : [`(${problemMessage(error, 'The log file could not be read.')})`]
  } catch {
    head = ['(The log file could not be read: no answer from the controller.)']
  }

  tailLoaded = true
  lines.value = capped([...head, ...pending])
  pending = []
  await scrollToEnd()
}

function capped(all: string[]): string[] {
  return all.length > MAX_LINES ? all.slice(all.length - MAX_LINES) : all
}

async function append(newLines: string[]) {
  if (!tailLoaded) {
    pending.push(...newLines)
    return
  }

  lines.value = capped([...lines.value, ...newLines])
  await scrollToEnd()
}

async function scrollToEnd() {
  await nextTick()
  if (consoleBox.value) {
    consoleBox.value.scrollTop = consoleBox.value.scrollHeight
  }
}

async function run(action: Action) {
  if (busy.value) {
    return
  }

  busy.value = action
  try {
    const { data, error } = await api.POST(actions[action].path)
    if (data) {
      message.success(data.message)
    } else {
      message.error(problemMessage(error, `${actions[action].label} did not work.`))
    }
  } catch {
    message.error(`${actions[action].label}: ${NO_ANSWER}`)
  } finally {
    busy.value = null
    void loadStatus()
  }
}

function confirmThen(title: string, question: string, positive: string, then: () => void) {
  dialog.warning({ title, content: question, positiveText: positive, negativeText: 'Cancel', onPositiveClick: then })
}

function ask(action: Action) {
  if (busy.value) {
    return
  }

  const question = actions[action].confirm
  if (question) {
    confirmThen(actions[action].label, question, actions[action].label, () => void run(action))
  } else {
    void run(action)
  }
}

/** Sends a console command: the typed one, or a button's (which says so when it worked). */
async function sendNow(text: string, button?: { busy: 'bot'; done: string }) {
  if (busy.value) {
    return
  }

  busy.value = button?.busy ?? 'command'
  try {
    const { data, error } = await api.POST('/api/server/command', { body: { command: text } })
    if (data) {
      if (button) {
        message.success(button.done)
      } else {
        command.value = ''
      }
    } else {
      message.error(problemMessage(error, 'The command was not sent.'))
    }
  } catch {
    message.error(`The command: ${NO_ANSWER}`)
  } finally {
    busy.value = null
  }
}

/** Sends the command; one that disconnects players asks first, like the buttons. */
function send() {
  const text = command.value.trim()
  if (!text || busy.value || running.value !== true) {
    return
  }

  if (disruptiveCommand(text)) {
    confirmThen('Send command', `"${text}" disconnects players. Send it?`, 'Send', () => void sendNow(text))
  } else {
    void sendNow(text)
  }
}

/**
 * The game's own console command for one more AI driver, as the Laravel page sent it. The
 * answer says the command was sent, not that a bot joined: a full server adds none.
 */
function addBot() {
  if (running.value === true) {
    void sendNow('/bot', { busy: 'bot', done: 'Sent /bot: an AI bot joins if there is room.' })
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
    <PageHeader title="Server control" description="Manage the server and follow its console output.">
      <template #status>
        <NTag :type="status?.isRunning ? 'success' : 'default'" round size="small">
        {{ status === null ? 'Unknown' : status.isRunning ? `Running (PID ${status.processId})` : 'Stopped' }}
        </NTag>
      </template>
    </PageHeader>

    <NAlert v-if="statusError" type="warning" :title="statusError" class="gap">
      The server's state is not known, so the actions are unavailable until it is.
    </NAlert>

    <!-- Every action needs a known state: Start and Update a stopped server (Update stops a
         running one without asking), the rest a running one. -->
    <NCard title="Actions" class="gap">
      <NSpace>
        <NButton type="primary" :loading="busy === 'start'" :disabled="!!busy || running !== false" @click="ask('start')">Start</NButton>
        <NButton :loading="busy === 'stop'" :disabled="!!busy || running !== true" @click="ask('stop')">Stop</NButton>
        <NButton :loading="busy === 'restart'" :disabled="!!busy || running !== true" @click="ask('restart')">Restart</NButton>
        <NButton :loading="busy === 'inject'" :disabled="!!busy || running !== true" @click="ask('inject')">Inject hook</NButton>
        <NButton :loading="busy === 'update'" :disabled="!!busy || running !== false" @click="ask('update')">Update</NButton>
        <NButton :loading="busy === 'bot'" :disabled="!!busy || running !== true" @click="addBot">Add AI bot</NButton>
      </NSpace>
      <NSpace class="danger">
        <NButton type="error" ghost :loading="busy === 'forcestop'" :disabled="!!busy || running !== true" @click="ask('forcestop')">
          Force stop
        </NButton>
        <!-- Like Force stop, only for a running server: stopped, it would just start it,
             under a confirmation that talks about killing a process. -->
        <NButton
          type="error"
          ghost
          :loading="busy === 'forcerestart'"
          :disabled="!!busy || running !== true"
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
          :input-props="{ 'aria-label': 'Console command' }"
          :disabled="!!busy || running !== true"
          @keyup.enter="send"
        />
        <NButton type="primary" :loading="busy === 'command'" :disabled="!!busy || running !== true || !command.trim()" @click="send">
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
