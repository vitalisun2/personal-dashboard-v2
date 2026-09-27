<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { chatApi, ProposalConflictError, type ChatEntityEntry, type ChatTurn, type ChatSource } from './chatApi'
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
const optimisticTurns = ref<Array<{ id: number; message: string; progress: string; failed?: boolean }>>([])
const scrollContainer = ref<HTMLElement | null>(null)
const inputElement = ref<HTMLTextAreaElement | null>(null)
let sendSequence = 0
let loadSequence = 0
let sendController: AbortController | undefined
const activeSendId = ref<number | null>(null)

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
  const loadId = ++loadSequence
  if (sendController) {
    sendSequence++
    sendController.abort()
    sendController = undefined
    activeSendId.value = null
    busy.value = false
  }
  busy.value = false
  activeId.value = id
  turns.value = []
  optimisticTurns.value = []
  error.value = ''
  if (!id) return
  busy.value = true
  try {
    const conversation = await chatApi.get(id, props.targetTurnId)
    if (loadSequence === loadId && activeId.value === id) { turns.value = conversation.turns; await scrollBottom() }
  } catch (cause) {
    if (loadSequence === loadId && activeId.value === id)
      error.value = cause instanceof Error ? cause.message : 'Не удалось загрузить чат.'
  } finally { if (loadSequence === loadId) busy.value = false }
}, { immediate: true })

onBeforeUnmount(() => {
  sendSequence++
  loadSequence++
  sendController?.abort()
  sendController = undefined
})

async function send() {
  const message = input.value.trim()
  if (!message || busy.value) return
  busy.value = true
  error.value = ''
  for (const failed of optimisticTurns.value.filter(turn => turn.failed)) {
    turns.value.push({
      id: `local-${failed.id}`,
      userMessage: failed.message,
      assistantMessage: '',
      scope: { mode: 'general' },
      requestedModel: 'Gemma',
      actualModel: 'Gemma',
      createdAt: new Date().toISOString(),
      sourceReferences: [],
      localFailure: failed.progress,
    })
  }
  optimisticTurns.value = optimisticTurns.value.filter(turn => !turn.failed)
  const sendId = ++sendSequence
  const controller = new AbortController()
  sendController = controller
  activeSendId.value = sendId
  optimisticTurns.value.push({ id: sendId, message, progress: 'Обрабатываю сообщение…' })
  input.value = ''
  await nextTick()
  resizeInput()
  await scrollBottom()
  let serverTurnId: string | undefined
  const isCurrent = () => sendSequence === sendId && sendController === controller
  try {
    const created = !activeId.value
    if (created) {
      const conversation = await chatApi.create(controller.signal)
      if (!isCurrent()) return
      activeId.value = conversation.id
    }
    const id = activeId.value!
    const scope = props.entity
      ? { mode: 'entity' as const, entityType: props.entity.entityType, entityId: props.entity.entityId, entityVersion: props.entity.entityVersion }
      : { mode: 'general' as const }
    const result = await chatApi.sendWithProgress(id, message, scope, (text, turnId) => {
      if (!isCurrent()) return
      const optimistic = optimisticTurns.value.find(turn => turn.id === sendId)
      if (optimistic) optimistic.progress = text
      if (turnId) serverTurnId = turnId
    }, controller.signal)
    if (!isCurrent()) return
    if (result.turn.proposalId) {
      for (const turn of turns.value) {
        if (turn.proposalStatus === 'Pending') turn.proposalStatus = 'Dismissed'
      }
    }
    turns.value.push(result.turn)
    optimisticTurns.value = optimisticTurns.value.filter(turn => turn.id !== sendId)
    if (created) {
      const query = { ...route.query }
      query.conversationId = id
      delete query.turnId
      try { await router.replace({ query }) } catch { /* saved turn remains visible */ }
    }
    await scrollBottom()
  } catch (cause) {
    if (!isCurrent() || controller.signal.aborted) return
    // A dropped stream may have completed on the server. Recover only by the
    // server-issued turn ID; matching user text could select an older turn.
    if (serverTurnId && activeId.value) {
      try {
        const conversation = await chatApi.get(activeId.value, serverTurnId)
        if (!isCurrent()) return
        const recovered = conversation.turns.find(turn => turn.id === serverTurnId)
        if (recovered) {
          if (recovered.proposalId) {
            for (const turn of turns.value) if (turn.proposalStatus === 'Pending') turn.proposalStatus = 'Dismissed'
          }
          turns.value.push(recovered)
          optimisticTurns.value = optimisticTurns.value.filter(turn => turn.id !== sendId)
          await scrollBottom()
          return
        }
      } catch { /* keep the original send error */ }
    }
    const optimistic = optimisticTurns.value.find(turn => turn.id === sendId)
    if (optimistic) {
      optimistic.failed = true
      optimistic.progress = 'Ответ не получен. Сообщение не отправлено повторно.'
    }
    error.value = cause instanceof Error ? cause.message : 'Не удалось отправить сообщение.'
  } finally {
    if (isCurrent()) {
      sendController = undefined
      activeSendId.value = null
      busy.value = false
    }
  }
}
async function newConversation() {
  if (busy.value && !sendController) return
  if (sendController) {
    sendSequence++
    sendController.abort()
    sendController = undefined
    activeSendId.value = null
    busy.value = false
  }
  turns.value = []
  optimisticTurns.value = []
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
    if (cause instanceof ProposalConflictError) {
      turn.proposalStatus = cause.conflict.expired ? 'Expired' : 'Stale'
      error.value = cause.conflict.error || (cause.conflict.expired
        ? 'Срок действия предложения истёк. Подготовьте новое предложение.'
        : 'Предложение устарело. Проверьте данные перед повторной попыткой.')
    } else error.value = cause instanceof Error ? cause.message : 'Не удалось обработать предложение.'
  } finally { busy.value = false }
}
function sourceUrl(url: string | null | undefined): string | undefined {
  return url?.startsWith('/') && !url.startsWith('//') ? url : undefined
}
function sourcePath(source: ChatSource): string {
  const module = source.kind?.split('.')[0]
  const section = module === 'knowledge' ? 'База знаний'
    : module === 'tasks' ? 'Задачи' : module === 'planning' ? 'Планирование' : ''
  return [section, source.path?.trim()].filter(Boolean).join(' / ')
}
function sourceProximity(source: ChatSource): string | null {
  const score = source.semanticSimilarity
  return typeof score === 'number' && Number.isFinite(score)
    ? Math.max(0, Math.min(1, score)).toFixed(2).replace('.', ',') : null
}
function validHighlight(source: ChatSource): { start: number; length: number } | null {
  const range = source.highlight
  return range && Number.isInteger(range.start) && Number.isInteger(range.length)
    && range.start >= 0 && range.length > 0 && range.start + range.length <= source.snippet.length
    ? range : null
}
function snippetParts(source: ChatSource): { before: string; match: string; after: string } | null {
  const range = validHighlight(source)
  return range ? {
    before: source.snippet.slice(0, range.start),
    match: source.snippet.slice(range.start, range.start + range.length),
    after: source.snippet.slice(range.start + range.length),
  } : null
}
function isShowResults(turn: ChatTurn): boolean {
  return !!turn.sourceDetails?.some(source => source.isShowResult)
}
function proposalStatusLabel(turn: ChatTurn): string {
  switch (turn.proposalStatus) {
    case 'Pending': return 'ожидает подтверждения'
    case 'Stale': return 'устарело, требуется новый просмотр'
    case 'Expired': return 'срок действия истёк'
    case 'Applied': {
      if (turn.changes?.some(change => change.entityType === 'knowledge.document')) return 'Документ создан'
      const task = turn.changes?.find(change => change.entityType === 'tasks.task')
      return task ? taskSuccessLabel(task) : 'Изменения применены'
    }
    case 'Dismissed': return 'отменено'
    default: return 'статус неизвестен'
  }
}
function taskDestination(change: { after: Record<string, unknown> }): { confirm: string; success: string } {
  const after = change.after
  const planning = after.planning && typeof after.planning === 'object' && !Array.isArray(after.planning)
    ? after.planning as Record<string, unknown> : null
  const placement = typeof after.placement === 'string' ? after.placement.toLowerCase() : ''
  if (placement === 'planned') {
    return { confirm: 'Добавить в план', success: 'Задача добавлена в план' }
  }
  if (placement === 'today') {
    return { confirm: 'Добавить на сегодня', success: 'Задача добавлена на сегодня' }
  }
  if (placement === 'backlog') {
    return { confirm: 'Добавить в бэклог', success: 'Задача добавлена в бэклог' }
  }
  if (planning && Object.values(planning).some(value => value !== null && value !== undefined && value !== '')) {
    return { confirm: 'Добавить в план', success: 'Задача добавлена в план' }
  }
  return {
    confirm: 'Добавить в бэклог',
    success: 'Задача добавлена в бэклог',
  }
}
function taskSuccessLabel(change: { after: Record<string, unknown> }): string {
  return taskDestination(change).success
}
function proposalConfirmLabel(turn: ChatTurn): string {
  const task = turn.changes?.find(change => change.entityType === 'tasks.task')
  if (task) return taskDestination(task).confirm
  return turn.changes?.some(change => change.entityType === 'knowledge.document') ? 'Создать документ' : 'Подтвердить'
}
function searchCoverageNote(turn: ChatTurn): string {
  // Saved search turns contain server diagnostics from both search passes.
  const diagnostics = turn.assistantMessage.toLowerCase()
  if (diagnostics.includes('смысловой поиск временно недоступен')) return 'Поиск по смыслу временно недоступен.'
  if (diagnostics.includes('проверка релевантности gemma недоступна')) return 'Результаты пока без проверки релевантности.'
  if (diagnostics.includes('смысловой индекс ещё')) return 'Поисковый индекс ещё обновляется.'
  return 'Показаны наиболее подходящие результаты.'
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
            <div v-if="turn.assistantMessage && (!isShowResults(turn) || !turn.sourceDetails?.length)" class="chat-bubble">{{ turn.assistantMessage }}</div>
            <div v-if="turn.localFailure" class="chat-bubble chat-pending-failed">{{ turn.localFailure }}</div>
            <template v-if="isShowResults(turn)" v-for="group in [
              { title: 'Прямые совпадения', sources: turn.sourceDetails?.filter(source => source.matchKind === 'lexical') ?? [] },
              { title: 'По смыслу', sources: turn.sourceDetails?.filter(source => source.matchKind !== 'lexical') ?? [] },
            ]" :key="group.title">
              <div v-if="group.sources.length" class="source-group">
                <h3 class="source-group-title">{{ group.title }}</h3>
                <div class="source-list">
                  <article v-for="(source, index) in group.sources" :key="`${source.kind}:${source.title}:${index}`" class="chat-source">
                    <div class="chat-source-heading"><span>{{ index + 1 }}.</span>
                      <a v-if="sourceUrl(source.url)" :href="sourceUrl(source.url)">{{ source.title }}</a>
                      <span v-else>{{ source.title }}</span>
                    </div>
                    <p class="chat-source-context"><template v-for="parts in [snippetParts(source)]"><template v-if="parts">{{ parts.before }}<mark>{{ parts.match }}</mark>{{ parts.after }}</template><template v-else>{{ source.snippet }}</template></template></p>
                    <span v-if="sourcePath(source)" class="chat-source-path">{{ sourcePath(source) }}</span>
                    <span v-if="sourceProximity(source) !== null" class="chat-source-proximity"
                      title="Близость по смыслу к запросу; не вероятность правильного ответа">Близость: {{ sourceProximity(source) }}</span>
                  </article>
                </div>
              </div>
            </template>
            <div v-if="!isShowResults(turn) && turn.sourceDetails?.length" class="source-list">
              <div v-for="(source, index) in turn.sourceDetails" :key="index" class="chat-source">
                <div class="chat-source-heading"><span>{{ index + 1 }}.</span>
                  <a v-if="sourceUrl(source.url)" :href="sourceUrl(source.url)">{{ source.title }}</a>
                  <span v-else>{{ source.title }}</span>
                </div>
                <span v-if="sourcePath(source)" class="chat-source-path">{{ sourcePath(source) }}</span>
                <span v-if="sourceProximity(source) !== null" class="chat-source-proximity">Близость: {{ sourceProximity(source) }}</span>
              </div>
            </div>
            <p v-if="isShowResults(turn)" class="chat-coverage-note">{{ searchCoverageNote(turn) }}</p>
            <div v-if="turn.proposalId && turn.changes?.length" class="proposal-card">
              <div class="proposal-heading">Предложение · {{ proposalStatusLabel(turn) }}</div>
              <div v-for="change in turn.changes" :key="change.id" class="proposal-preview">
                <strong>{{ change.displayName }}</strong>
                <p>{{ change.preview }}</p>
                <a v-if="turn.proposalStatus === 'Applied' && change.entityType === 'knowledge.document'" :href="`/knowledge/${change.entityId}`">Открыть документ</a>
                <a v-if="turn.proposalStatus === 'Applied' && change.entityType === 'tasks.task'" :href="`/tasks/${change.entityId}`">Открыть задачу</a>
              </div>
              <div v-if="turn.proposalStatus === 'Pending'" class="proposal-actions">
                <button type="button" class="confirm-button" :disabled="busy" @click="actOnProposal(turn, true)">{{ proposalConfirmLabel(turn) }}</button>
                <button type="button" class="proposal-dismiss" :disabled="busy" @click="actOnProposal(turn, false)">Отменить</button>
              </div>
            </div>
          </div></div>
        </template>
        <template v-for="pending in optimisticTurns" :key="`pending-${pending.id}`">
          <div class="chat-row user"><div class="chat-bubble">{{ pending.message }}</div></div>
          <div class="chat-row agent"><div class="chat-answer">
            <div class="chat-bubble chat-pending-bubble" role="status" aria-live="polite" aria-atomic="true">
              <span>{{ pending.progress }}</span><span v-if="!pending.failed" class="chat-pending-dots" aria-hidden="true"><span v-for="dot in 3" :key="dot">•</span></span>
            </div>
          </div></div>
        </template>
        <p v-if="busy && activeSendId === null" class="chat-state">Подождите…</p>
        <p v-if="error" class="chat-error" role="alert">{{ error }}</p>
      </div>
    </div>
    <form class="chat-composer" @submit.prevent="send">
      <textarea ref="inputElement" v-model="input" class="chat-input" rows="1" placeholder="Сообщение агенту…" aria-label="Сообщение агенту" @keydown="onInputKeydown" />
      <button type="submit" class="chat-send" :disabled="busy || !input.trim()" aria-label="Отправить">↑</button>
    </form>
  </section>
</template>
