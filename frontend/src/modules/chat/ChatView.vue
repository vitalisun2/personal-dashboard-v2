<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import type { ChatEntityEntry } from './chatApi'
import './chatView.css'

defineProps<{
  conversationId?: string
  targetTurnId?: string
  entity?: ChatEntityEntry
}>()

const emit = defineEmits<{ (event: 'back'): void }>()
const messages = ref<{ id: number; user: string; assistant: string }[]>([])
const input = ref('')
const scrollContainer = ref<HTMLElement | null>(null)
const inputElement = ref<HTMLTextAreaElement | null>(null)

function resizeInput() {
  const el = inputElement.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 112)}px`
  el.classList.toggle('is-overflowing', el.scrollHeight > 112)
}

async function send() {
  const message = input.value.trim()
  if (!message) return
  messages.value.push({ id: Date.now() + messages.value.length, user: message, assistant: 'Функционал не реализован' })
  input.value = ''
  await nextTick()
  resizeInput()
  if (scrollContainer.value) scrollContainer.value.scrollTop = scrollContainer.value.scrollHeight
}

function newConversation() {
  messages.value = []
  input.value = ''
  void nextTick(resizeInput)
}

function onInputKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); void send() }
}

watch(input, () => { void nextTick(resizeInput) })
</script>

<template>
  <section class="chat-view" aria-label="Чат с агентом">
    <div class="chat-back-row">
      <button type="button" class="doc-action doc-back" @click="emit('back')">← Назад</button>
      <div class="chat-back-actions">
        <button type="button" class="chat-new-btn" aria-label="Новый чат" title="Новый чат" @click="newConversation">＋</button>
      </div>
    </div>

    <div ref="scrollContainer" class="chat-scroll">
      <div class="chat-list" aria-live="polite">
        <template v-for="message in messages" :key="message.id">
          <div class="chat-row user"><div class="chat-bubble">{{ message.user }}</div></div>
          <div class="chat-row agent"><div class="chat-answer"><div class="chat-bubble">{{ message.assistant }}</div></div></div>
        </template>
      </div>
    </div>

    <form class="chat-composer" @submit.prevent="send">
      <textarea ref="inputElement" v-model="input" class="chat-input" rows="1" placeholder="Сообщение агенту…" aria-label="Сообщение агенту" @keydown="onInputKeydown" />
      <button type="submit" class="chat-send" :disabled="!input.trim()" aria-label="Отправить">↑</button>
    </form>
  </section>
</template>
