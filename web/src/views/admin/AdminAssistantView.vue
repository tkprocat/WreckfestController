<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'
import { NAlert, NButton, NCard, NEmpty, NInput, NSkeleton } from 'naive-ui'
import { api } from '@/api/client'
import { problemMessage } from '@/api/problems'
import PageHeader from '@/components/PageHeader.vue'
import ChatMarkdown from '@/components/ChatMarkdown.vue'
import { applyProposal, pageFor, type Proposal } from './assistant/applyProposal'

/**
 * The admin AI assistant: the public one's lookups, plus the rotation, collections, cups
 * and server settings. It proposes changes and writes nothing; each proposal has an Apply
 * button that sends it through the admin API, so it is checked like any edit. The
 * conversation lives in this page only.
 */
type ProposalState = Proposal & { state: 'open' | 'applying' | 'applied' | 'failed'; outcome?: string }

interface Turn {
  role: 'user' | 'assistant'
  content: string
  proposals: ProposalState[]
}

/** As the API allows (AssistantController.MaxMessages and MaxMessageLength). */
const MAX_MESSAGES = 20
const MAX_LENGTH = 2000

const SUGGESTIONS = [
  'What is in the rotation now?',
  'Make a collection of every derby arena.',
  'Set up a cup every Friday at 20:00 UTC.',
  'Turn bots off and allow 20 players.',
]

const checking = ref(true)
const available = ref(false)
const turns = ref<Turn[]>([])
const draft = ref('')
const sending = ref(false)
const error = ref<string | null>(null)
const log = ref<HTMLElement | null>(null)

const canSend = computed(() => !sending.value && draft.value.trim().length > 0 && draft.value.length <= MAX_LENGTH)

/**
 * What the model is sent back: its own replies with what it proposed and what became of
 * it, so a follow-up can build on what the admin applied.
 */
function history() {
  return turns.value.slice(-MAX_MESSAGES).map((turn) => {
    if (!turn.proposals.length) return { role: turn.role, content: turn.content }
    const lines = turn.proposals.map((p) => `- ${p.summary} (${p.state === 'applied' ? 'applied' : 'not applied'})`)
    const content = `${turn.content}\n\nProposed:\n${lines.join('\n')}`
    return { role: turn.role, content: content.length > MAX_LENGTH ? turn.content : content }
  })
}

async function scrollToEnd() {
  await nextTick()
  log.value?.lastElementChild?.scrollIntoView?.({ block: 'nearest' })
}

async function load() {
  try {
    const { data } = await api.GET('/api/assistant')
    available.value = data?.available ?? false
  } catch {
    available.value = false
  }
}

async function ask(question = draft.value) {
  const text = question.trim()
  if (!text || sending.value) return

  turns.value = [...turns.value, { role: 'user', content: text, proposals: [] }]
  draft.value = ''
  error.value = null
  sending.value = true
  void scrollToEnd()
  try {
    const { data, error: problem, response } = await api.POST('/api/assistant/chat', { body: { messages: history() } })
    if (data) {
      turns.value = [
        ...turns.value,
        { role: 'assistant', content: data.reply, proposals: data.proposals.map((p) => ({ ...p, state: 'open' as const })) },
      ]
    } else {
      fail(text, response.status === 404 ? 'The AI assistant has no API key. Set one under Settings.' : problemMessage(problem, 'The AI assistant could not answer. Try again.'))
      if (response.status === 404) await load()
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
  turns.value = turns.value.slice(0, -1)
  draft.value = question
  error.value = message
}

async function apply(proposal: ProposalState) {
  if (proposal.state === 'applying' || proposal.state === 'applied') return
  proposal.state = 'applying'
  proposal.outcome = undefined
  const outcome = await applyProposal(proposal)
  proposal.state = outcome.ok ? 'applied' : 'failed'
  proposal.outcome = outcome.message
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault()
    void ask()
  }
}

function clear() {
  turns.value = []
  error.value = null
}

onMounted(async () => {
  await load()
  checking.value = false
})
</script>

<template>
  <section class="admin-assistant">
    <PageHeader
      title="AI assistant"
      description="Ask about the server, or ask for changes to the rotation, collections, cups and server settings. Nothing changes until you apply it."
    />

    <NSkeleton v-if="checking" class="skeleton" />
    <NCard v-else-if="!available && turns.length === 0">
      <NEmpty description="The AI assistant has no API key.">
        <template #extra>
          <RouterLink :to="{ name: 'admin-settings' }">Set one under Settings</RouterLink>
        </template>
      </NEmpty>
    </NCard>
    <template v-else>
      <ol v-if="turns.length" ref="log" class="log" aria-label="Conversation" aria-live="polite">
        <li v-for="(turn, index) in turns" :key="index" :class="['message', turn.role]">
          <span class="who">{{ turn.role === 'user' ? 'You' : 'Assistant' }}</span>
          <ChatMarkdown v-if="turn.role === 'assistant'" class="text reply" :text="turn.content" />
          <p v-else class="text">{{ turn.content }}</p>
          <ul v-if="turn.proposals.length" class="proposals" aria-label="Proposed changes">
            <li v-for="(proposal, p) in turn.proposals" :key="p" :class="['proposal', proposal.state]">
              <p class="summary">{{ proposal.summary }}</p>
              <div class="proposal-actions">
                <NButton
                  v-if="proposal.state !== 'applied'"
                  size="small"
                  type="primary"
                  :loading="proposal.state === 'applying'"
                  @click="apply(proposal)"
                >
                  {{ proposal.state === 'failed' ? 'Try again' : 'Apply' }}
                </NButton>
                <span v-else class="applied" role="status">
                  Applied<template v-if="pageFor(proposal.kind)">.
                    <RouterLink :to="{ name: pageFor(proposal.kind)!.name }">Open {{ pageFor(proposal.kind)!.label }}</RouterLink>
                  </template>
                </span>
              </div>
              <p v-if="proposal.state === 'failed'" class="outcome" role="alert">{{ proposal.outcome }}</p>
            </li>
          </ul>
        </li>
        <li v-if="sending" class="message assistant pending" role="status">
          <span class="who">Assistant</span>
          <p class="text">Working on it…</p>
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
          placeholder="Ask a question or for a change"
          :input-props="{ 'aria-label': 'Your question' }"
          @keydown="onKeydown"
        />
        <div class="actions">
          <NButton v-if="turns.length" quaternary :disabled="sending" @click="clear">Start over</NButton>
          <NButton type="primary" attr-type="submit" :loading="sending" :disabled="!canSend">Ask</NButton>
        </div>
      </form>
    </template>
  </section>
</template>

<style scoped>
.admin-assistant { max-width: var(--content-form); }
.skeleton { height: 160px; border-radius: 12px; }
.log { list-style: none; margin: 0 0 24px; padding: 0; display: flex; flex-direction: column; gap: 16px; }
.message { max-width: 85%; padding: 16px 20px; border-radius: 12px; background: var(--surface); }
.message.user { align-self: flex-end; background: var(--page-background); border: 1px solid var(--surface); }
.message.pending .text { color: var(--text-muted); }
.who { display: block; margin-bottom: 4px; font-size: var(--font-label); font-weight: 650; color: var(--text-secondary); }
.text { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.text.reply { white-space: normal; }
.proposals { list-style: none; margin: 12px 0 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
.proposal { padding: 12px 16px; border-radius: 8px; border: 1px solid var(--text-muted); background: var(--page-background); }
.summary { margin: 0 0 8px; overflow-wrap: anywhere; }
.proposal-actions { display: flex; align-items: center; gap: 8px; }
.applied { color: var(--text-secondary); font-size: var(--font-support); }
.outcome { margin: 8px 0 0; color: var(--text-secondary); font-size: var(--font-support); }
.suggestions { list-style: none; display: flex; flex-wrap: wrap; gap: 12px; margin: 0 0 24px; padding: 0; }
.notice { margin-bottom: 16px; }
.composer { display: flex; flex-direction: column; gap: 12px; }
.actions { display: flex; justify-content: flex-end; gap: 12px; }
@media (max-width: 760px) {
  .message { max-width: 100%; }
}
</style>
