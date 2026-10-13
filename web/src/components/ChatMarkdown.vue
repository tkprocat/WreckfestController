<script setup lang="ts">
import { computed } from 'vue'
import MarkdownIt from 'markdown-it'

/**
 * An AI assistant reply's Markdown. Replies can quote player and track names, which anyone
 * can choose, so: raw HTML is escaped (html: false), and links and images are off, so a
 * name cannot become a link or load anything. What is left is text formatting - bold,
 * lists, tables, code - which markdown-it escapes, so v-html gets no markup from the reply.
 */
const md = new MarkdownIt({ html: false, linkify: false, typographer: false, breaks: true })
  .disable(['link', 'image', 'autolink'])

const props = defineProps<{ text: string }>()

const html = computed(() => md.render(props.text))
</script>

<template>
  <div class="chat-markdown" v-html="html" />
</template>

<style scoped>
.chat-markdown { overflow-wrap: anywhere; }
.chat-markdown > :deep(:first-child) { margin-top: 0; }
.chat-markdown > :deep(:last-child) { margin-bottom: 0; }
.chat-markdown :deep(p),
.chat-markdown :deep(ul),
.chat-markdown :deep(ol),
.chat-markdown :deep(pre),
.chat-markdown :deep(blockquote),
.chat-markdown :deep(table) { margin: 8px 0; }
.chat-markdown :deep(ul),
.chat-markdown :deep(ol) { padding-left: 24px; }
.chat-markdown :deep(li + li) { margin-top: 4px; }
/* A reply is a message, not a page: headings stay at text size. */
.chat-markdown :deep(:is(h1, h2, h3, h4, h5, h6)) { margin: 12px 0 4px; font-size: inherit; font-weight: 650; }
.chat-markdown :deep(strong) { font-weight: 650; }
.chat-markdown :deep(code) { font-family: var(--font-family-mono, Consolas, monospace); font-size: .92em; padding: 1px 4px; border-radius: 4px; background: var(--page-background); }
.chat-markdown :deep(pre) { padding: 8px 12px; border-radius: 8px; background: var(--page-background); overflow-x: auto; }
.chat-markdown :deep(pre code) { padding: 0; background: none; }
.chat-markdown :deep(blockquote) { padding-left: 12px; border-left: 3px solid var(--text-muted); color: var(--text-secondary); }
.chat-markdown :deep(table) { display: block; max-width: 100%; overflow-x: auto; border-collapse: collapse; font-size: var(--font-support); }
.chat-markdown :deep(:is(th, td)) { padding: 4px 12px 4px 0; text-align: left; border-bottom: 1px solid var(--page-background); }
.chat-markdown :deep(th) { font-weight: 650; color: var(--text-secondary); }
</style>
