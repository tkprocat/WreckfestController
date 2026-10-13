<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { NAlert, NButton, NEmpty, NInput, NSkeleton } from 'naive-ui'
import { api } from '@/api/client'
import { problemMessage } from '@/api/problems'
import type { components } from '@/api/schema'
import { useAssistantStatus } from '@/composables/useAssistantStatus'
import ChatMarkdown from '@/components/ChatMarkdown.vue'

/**
 * The public AI assistant: a visitor asks about the server in plain language. The
 * controller asks the model, which looks the answers up in the same public data the
 * other pages show. The conversation lives in this page only; reloading starts afresh.
 */
type Message = components['schemas']['AssistantChatMessage']

/** As the API allows (AssistantController.MaxMessages and MaxMessageLength). */
const MAX_MESSAGES = 20
const MAX_LENGTH = 2000

const SUGGESTIONS = ['Who is online right now?', 'What is next in the rotation?', 'Who won the last race?', 'When is the next cup?']

const status = useAssistantStatus()
const checking = ref(true)
const messages = ref<Message[]>([])
const draft = ref('')
const sending = ref(false)
const error = ref<string | null>(null)
const log = ref<HTMLElement | null>(null)

const canSend = computed(() => !sending.value && draft.value.trim().length > 0 && draft.value.length <= MAX_LENGTH)

async function scrollToEnd() {
  await nextTick()
  log.value?.lastElementChild?.scrollIntoView?.({ block: 'nearest' })
}

async function ask(question = draft.value) {
  const text = question.trim()
  if (!text || sending.value) return

  messages.value = [...messages.value, { role: 'user', content: text }]
  draft.value = ''
  error.value = null
  sending.value = true
  void scrollToEnd()
  try {
    const { data, error: problem, response } = await api.POST('/api/public/assistant/chat', {
      body: { messages: messages.value.slice(-MAX_MESSAGES) },
    })
    if (data) {
      messages.value = [...messages.value, { role: 'assistant', content: data.reply }]
    } else {
      fail(text, response.status === 404 ? 'The AI assistant is turned off.' : problemMessage(problem, 'The AI assistant could not answer. Try again.'))
      if (response.status === 404) await status.load()
    }
  } catch {
    fail(text, 'No answer from the server. Check your connection and try again.')
  } finally {
    sending.value = false
    void scrollToEnd()
  }
}

/** Takes the unanswered question back into the box, so trying again is one click. */
function fail(question: string, message: string) {
  messages.value = messages.value.slice(0, -1)
  draft.value = question
  error.value = message
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void ask()
  }
}

function clear() {
  messages.value = []
  error.value = null
}

onMounted(async () => {
  await status.load()
  checking.value = false
})
</script>

<template>
  <section class="assistant spectator" aria-labelledby="assistant-title">
    <header class="page-head">
      <p class="eyebrow">Ask the server</p>
      <h1 id="assistant-title">AI assistant</h1>
      <p class="lede">Ask about who is playing, the rotation, cups, results and tracks. Answers come from this server's public pages and can be wrong.</p>
    </header>

    <NSkeleton v-if="checking" class="skeleton" />
    <NEmpty v-else-if="!status.enabled.value && messages.length === 0" description="The AI assistant is turned off." />
    <template v-else>
      <ol v-if="messages.length" ref="log" class="log" aria-label="Conversation" aria-live="polite">
        <li v-for="(message, index) in messages" :key="index" :class="['message', message.role]">
          <span class="who">{{ message.role === 'user' ? 'You' : 'Assistant' }}</span>
          <ChatMarkdown v-if="message.role === 'assistant'" class="text reply" :text="message.content" />
          <p v-else class="text">{{ message.content }}</p>
        </li>
        <li v-if="sending" class="message assistant pending" role="status">
          <span class="who">Assistant</span>
          <p class="text">Looking that up…</p>
        </li>
      </ol>
      <ul v-else class="suggestions" aria-label="Suggested questions">
        <li v-for="suggestion in SUGGESTIONS" :key="suggestion">
          <NButton round :disabled="sending" @click="ask(suggestion)">{{ suggestion }}</NButton>
        </li>
      </ul>

      <NAlert v-if="error" type="warning" :title="error" class="notice" />

      <form class="composer" @submit.prevent="ask()">
        <NInput
          v-model:value="draft"
          type="textarea"
          :autosize="{ minRows: 2, maxRows: 6 }"
          :maxlength="MAX_LENGTH"
          placeholder="Ask a question"
          :input-props="{ 'aria-label': 'Your question' }"
          @keydown="onKeydown"
        />
        <div class="actions">
          <NButton v-if="messages.length" quaternary :disabled="sending" @click="clear">Start over</NButton>
          <NButton type="primary" attr-type="submit" :loading="sending" :disabled="!canSend">Ask</NButton>
        </div>
      </form>
    </template>
  </section>
</template>

<style scoped>
.spectator { color: var(--page-text); font-size: var(--font-body); line-height: var(--line-reading); max-width: var(--content-form); }
.page-head { margin-bottom: 24px; }
.eyebrow { margin: 0; font-size: var(--font-label); font-weight: 650; letter-spacing: .06em; text-transform: uppercase; color: var(--text-secondary); }
h1 { margin: 8px 0 0; font-size: var(--font-title); line-height: 1.25; }
.lede { margin: 8px 0 0; color: var(--text-secondary); font-size: var(--font-support); }
.skeleton { height: 160px; border-radius: 12px; }
.log { list-style: none; margin: 0 0 24px; padding: 0; display: flex; flex-direction: column; gap: 16px; }
.message { max-width: 85%; padding: 16px 20px; border-radius: 12px; background: var(--surface); }
.message.user { align-self: flex-end; background: var(--page-background); border: 1px solid var(--surface); }
.message.pending .text { color: var(--text-muted); }
.who { display: block; margin-bottom: 4px; font-size: var(--font-label); font-weight: 650; color: var(--text-secondary); }
.text { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.text.reply { white-space: normal; }
.suggestions { list-style: none; display: flex; flex-wrap: wrap; gap: 12px; margin: 0 0 24px; padding: 0; }
.notice { margin-bottom: 16px; }
.composer { display: flex; flex-direction: column; gap: 12px; }
.actions { display: flex; justify-content: flex-end; gap: 12px; }
@media (max-width: 760px) {
  .message { max-width: 100%; }
}
</style>
