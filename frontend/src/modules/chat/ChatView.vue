<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { chatApi, ProposalConflictError, type ChatEntityEntry, type ChatTurn } from './chatApi'
import './chatView.css'

const props = defineProps<{ conversationId?: string; targetTurnId?: string; entity?: ChatEntityEntry }>()
const emit = defineEmits<{ (event: 'back'): void }>()
const route = useRoute()
const router = useRouter()
const turns = ref<ChatTurn[]>([])
const activeId = ref<string>()
const input = ref('')
const busy = ref(false)
const error = ref('')
const scrollContainer = ref<HTMLElement | null>(null)
const inputElement = ref<HTMLTextAreaElement | null>(null)

function resizeInput() {
  const el = inputElement.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 112)}px`
  el.classList.toggle('is-overflowing', el.scrollHeight > 112)
}
async function scrollBottom() {
  await nextTick()
  if (scrollContainer.value) scrollContainer.value.scrollTop = scrollContainer.value.scrollHeight
}
watch(() => [props.conversationId, props.targetTurnId] as const, async ([id]) => {
  activeId.value = id
  turns.value = []
  error.value = ''
  if (!id) return
  busy.value = true
  try {
    const conversation = await chatApi.get(id, props.targetTurnId)
    if (activeId.value === id) { turns.value = conversation.turns; await scrollBottom() }
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Не удалось загрузить чат.'
  } finally { busy.value = false }
}, { immediate: true })
async function send() {
  const message = input.value.trim()
  if (!message || busy.value) return
  busy.value = true
  error.value = ''
  try {
    const created = !activeId.value
    if (created) activeId.value = (await chatApi.create()).id
    const id = activeId.value!
    const scope = props.entity
      ? { mode: 'entity' as const, entityType: props.entity.entityType, entityId: props.entity.entityId, entityVersion: props.entity.entityVersion }
      : { mode: 'general' as const }
    const result = await chatApi.send(id, message, scope)
    turns.value.push(result.turn)
    if (created) {
      const query = { ...route.query }
      query.conversationId = id
      delete query.turnId
      await router.replace({ query })
    }
    input.value = ''
    await nextTick()
    resizeInput()
    await scrollBottom()
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Не удалось отправить сообщение.'
  } finally { busy.value = false }
}
async function newConversation() {
  if (busy.value) return
  turns.value = []
  activeId.value = undefined
  input.value = ''
  error.value = ''
  const query = { ...route.query }
  delete query.conversationId
  delete query.turnId
  await router.replace({ query })
  await nextTick(resizeInput)
}
async function actOnProposal(turn: ChatTurn, confirm: boolean) {
  if (!activeId.value || !turn.proposalId || busy.value) return
  busy.value = true
  error.value = ''
  try {
    if (confirm) await chatApi.confirm(activeId.value, turn.proposalId)
    else await chatApi.dismissProposal(activeId.value, turn.proposalId)
    turn.proposalStatus = confirm ? 'Applied' : 'Dismissed'
  } catch (cause) {
    error.value = cause instanceof ProposalConflictError
      ? cause.conflict.error || 'Предложение устарело. Проверьте данные перед повторной попыткой.'
      : cause instanceof Error ? cause.message : 'Не удалось обработать предложение.'
  } finally { busy.value = false }
}
function sourceUrl(url: string | null | undefined): string | undefined {
  return url?.startsWith('/') && !url.startsWith('//') ? url : undefined
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
        <template v-for="turn in turns" :key="turn.id">
          <div class="chat-row user"><div class="chat-bubble">{{ turn.userMessage }}</div></div>
          <div class="chat-row agent"><div class="chat-answer" :class="{ 'target-turn': turn.id === targetTurnId }">
            <div class="chat-bubble">{{ turn.assistantMessage }}</div>
            <div v-if="turn.sourceDetails?.length" class="source-list">
              <template v-for="(source, index) in turn.sourceDetails" :key="index">
                <a v-if="sourceUrl(source.url)" :href="sourceUrl(source.url)">{{ source.title }}</a>
                <span v-else>{{ source.title }}</span>
              </template>
            </div>
            <div v-if="turn.proposalId && turn.changes?.length" class="proposal-card">
              <div class="proposal-heading">Предложение · {{ turn.proposalStatus === 'Pending' ? 'ожидает подтверждения' : turn.proposalStatus }}</div>
              <p v-for="change in turn.changes" :key="change.id">{{ change.displayName }}: {{ change.preview }}</p>
              <div v-if="turn.proposalStatus === 'Pending'" class="proposal-actions">
                <button type="button" class="confirm-button" :disabled="busy" @click="actOnProposal(turn, true)">Подтвердить</button>
                <button type="button" class="proposal-dismiss" :disabled="busy" @click="actOnProposal(turn, false)">Отклонить</button>
              </div>
            </div>
          </div></div>
        </template>
        <p v-if="busy" class="chat-state">Подождите…</p>
        <p v-if="error" class="chat-error" role="alert">{{ error }}</p>
      </div>
    </div>
    <form class="chat-composer" @submit.prevent="send">
      <textarea ref="inputElement" v-model="input" class="chat-input" rows="1" placeholder="Сообщение агенту…" aria-label="Сообщение агенту" :disabled="busy" @keydown="onInputKeydown" />
      <button type="submit" class="chat-send" :disabled="busy || !input.trim()" aria-label="Отправить">↑</button>
    </form>
  </section>
</template>
