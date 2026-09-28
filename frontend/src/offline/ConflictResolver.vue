<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { listEntityConflicts, resolveEntityConflict, type EntityConflict } from './conflicts'
import { subscribeSyncStatus } from './runtime'

const conflicts = ref<EntityConflict[]>([])
const selected = ref<EntityConflict | null>(null)
const tab = ref<'local' | 'server'>('local')
const dialog = ref<HTMLDialogElement | null>(null)
const trigger = ref<HTMLButtonElement | null>(null)
const busy = ref(false)
const opening = ref(false)
const error = ref('')
let unsubscribe: (() => void) | undefined
let generation = 0
const key = (item: EntityConflict) => item.order?.scope ?? `${item.type}:${item.id}`
const entityLabels: Record<string, string> = { 'planning.project': 'Проект', 'planning.milestone': 'Эпик', 'planning.feature': 'Фича', 'tasks.task': 'Задача', 'tasks.section': 'Раздел задач', 'tasks.groupOrder': 'Порядок разделов', 'knowledge.document': 'Документ', 'knowledge.section': 'Раздел базы знаний' }
const entityLabel = computed(() => {
  if (!selected.value) return ''
  if (selected.value.type === 'knowledge.node') {
    const data = (selected.value.local || selected.value.server) as { kind?: string } | null
    return data?.kind === 'section' ? 'Раздел базы знаний' : 'Документ'
  }
  return entityLabels[selected.value.type] || 'Элемент'
})
const labels: Record<string, string> = {
  title: 'Название', name: 'Название', description: 'Описание', markdown: 'Текст документа',
  content: 'Содержимое', body: 'Описание', kind: 'Тип', location: 'Список', placement: 'Список', bucket: 'Список', workStatus: 'Статус задачи',
  status: 'Статус', featureStatus: 'Статус фичи', isArchived: 'В архиве', archivedSectionName: 'Раздел архива',
  path: 'Путь', position: 'Позиция', milestones: 'Эпики', features: 'Фичи', keys: 'Порядок разделов',
  planning: 'Связь с планированием', parentId: 'Родительский раздел', sectionId: 'Раздел',
  projectId: 'Проект', milestoneId: 'Эпик', featureId: 'Фича', url: 'Ссылка', archived: 'В архиве',
}
const values: Record<string, string> = { backlog: 'Backlog', today: 'Сегодня', planned: 'В плане', archived: 'Архив', new: 'Новая', inprogress: 'В работе', done: 'Готово', active: 'В работе', document: 'Документ', section: 'Раздел' }
function printable(value: unknown): string {
  if (value === null || value === undefined || value === '') return '—'
  if (typeof value === 'boolean') return value ? 'Да' : 'Нет'
  if (typeof value === 'object') return JSON.stringify(value, null, 2)
  return String(value)
}
const previewDeleted = computed(() => selected.value && (tab.value === 'local' ? selected.value.localDeleted : selected.value.serverDeleted))
const orderItems = computed(() => selected.value?.order?.[tab.value] || [])
const fields = computed(() => {
  const payload = selected.value && (tab.value === 'local' ? selected.value.local : selected.value.server)
  if (payload === null || payload === undefined) return []
  if (typeof payload !== 'object' || Array.isArray(payload)) return [{ label: 'Содержимое', value: printable(payload) }]
  const data = payload as Record<string, unknown>
  const aliases: Record<string, string> = { body: 'description', placement: 'location', bucket: 'location', featureStatus: 'status', archived: 'isArchived', name: 'title' }
  const entries = Object.entries(data).filter(([name]) => !['id', 'version', 'expectedVersion', 'expectedParentVersion', 'operation', 'updatedAt', 'updatedAtUtc', 'progressPercent', 'milestones', 'features'].includes(name) && !(aliases[name] && data[aliases[name]] !== undefined))
  const priority = ['title', 'name', 'description', 'markdown', 'content']
  entries.sort(([a], [b]) => (priority.includes(a) ? priority.indexOf(a) : 100) - (priority.includes(b) ? priority.indexOf(b) : 100))
  return entries.map(([name, value]) => ({ label: labels[name] || name, value: ['location', 'placement', 'workStatus', 'status', 'featureStatus', 'kind'].includes(name) && typeof value === 'string' ? values[value.toLowerCase()] || value : printable(value) }))
})
async function reload() {
  const current = ++generation
  try {
    const items = await listEntityConflicts()
    if (current === generation) conflicts.value = items
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось прочитать конфликты.' }
}
function pick(item: EntityConflict) {
  selected.value = item
  tab.value = 'local'
  error.value = ''
}
async function open() {
  if (opening.value) return
  opening.value = true
  try {
    await reload()
    if (!conflicts.value.length) return
    pick(conflicts.value[0])
    await nextTick()
    dialog.value?.showModal()
  } finally { opening.value = false }
}
function close() {
  dialog.value?.close()
  trigger.value?.focus()
}
function switchTab(event: KeyboardEvent) {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key) || busy.value) return
  event.preventDefault()
  tab.value = event.key === 'Home' ? 'local' : event.key === 'End' ? 'server' : tab.value === 'local' ? 'server' : 'local'
  void nextTick(() => dialog.value?.querySelector<HTMLButtonElement>(`#conflict-${tab.value}-tab`)?.focus())
}
async function choose() {
  if (!selected.value || busy.value || selected.value.order?.error) return
  busy.value = true
  error.value = ''
  const resolvedKey = key(selected.value)
  try {
    await resolveEntityConflict(selected.value, tab.value)
    await reload()
    const next = conflicts.value.find(item => key(item) !== resolvedKey) || conflicts.value[0]
    if (next) pick(next)
    else { selected.value = null; close() }
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось сохранить выбор. Конфликт остаётся нерешённым.' }
  finally { busy.value = false }
}
function onDataUpdated() { void reload() }
onMounted(() => {
  unsubscribe = subscribeSyncStatus(() => { void reload() })
  window.addEventListener('offline-data-updated', onDataUpdated)
})
onBeforeUnmount(() => {
  unsubscribe?.()
  window.removeEventListener('offline-data-updated', onDataUpdated)
})
</script>

<template>
  <button v-if="conflicts.length" ref="trigger" class="conflict-trigger" type="button" :disabled="opening" @click="open">
    {{ opening ? 'Загружаем версии…' : `Разобрать конфликты · ${conflicts.length}` }}
  </button>
  <dialog ref="dialog" class="conflict-dialog" aria-labelledby="conflict-heading" @close="selected = null" @click="event => { if (event.target === dialog && !busy) close() }">
    <div class="conflict-dialog-inner">
      <header class="conflict-header">
        <h2 id="conflict-heading">{{ selected?.order ? 'Выберите порядок' : 'Выберите версию' }}</h2>
        <button type="button" class="conflict-close" aria-label="Закрыть без выбора" @click="close">×</button>
      </header>
      <p v-if="selected?.order" class="conflict-hint">Порядок элементов изменился на нескольких устройствах. Выберите последовательность. Изменится только порядок: названия, описания и содержимое сохранятся. Пока вы не выбрали, конфликт остаётся нерешённым.</p>
      <p v-else class="conflict-hint">Данные изменились на нескольких устройствах. Сравните версии и выберите, какую сохранить целиком. Пока вы не выбрали, обе версии сохранены.</p>
      <div v-if="conflicts.length > 1" class="conflict-list" aria-label="Конфликтующие элементы">
        <button v-for="item in conflicts" :key="key(item)" type="button" :class="{ active: selected && key(selected) === key(item) }" :disabled="busy" @click="pick(item)">{{ item.title }}</button>
      </div>
      <template v-if="selected">
        <h3 class="conflict-title">{{ selected.order ? selected.title : `${entityLabel} · ${selected.title}` }}</h3>
        <div class="conflict-tabs" role="tablist" aria-label="Версия данных" @keydown="switchTab">
          <button id="conflict-local-tab" type="button" role="tab" :tabindex="tab === 'local' ? 0 : -1" :aria-selected="tab === 'local'" aria-controls="conflict-preview" :disabled="busy" @click="tab = 'local'">На этом устройстве</button>
          <button id="conflict-server-tab" type="button" role="tab" :tabindex="tab === 'server' ? 0 : -1" :aria-selected="tab === 'server'" aria-controls="conflict-preview" :disabled="busy" @click="tab = 'server'">На сервере</button>
        </div>
        <section id="conflict-preview" class="conflict-preview" role="tabpanel" :aria-labelledby="`conflict-${tab}-tab`" tabindex="0">
          <template v-if="selected.order">
            <p v-if="selected.order.error" class="conflict-error" role="alert">{{ selected.order.error }} Закройте окно и откройте конфликт заново, чтобы обновить список.</p>
            <ol v-else-if="orderItems.length" class="conflict-order" :aria-label="tab === 'local' ? 'Порядок на этом устройстве' : 'Порядок на сервере'">
              <li v-for="item in orderItems" :key="item.id">{{ item.title || 'Без названия' }}</li>
            </ol>
            <p v-else>В этом списке нет элементов.</p>
            <div v-if="!selected.order.error" class="conflict-order-notes">
              <p v-if="selected.order.addedTitles?.length">Новые элементы с сервера добавлены в конец порядка устройства: {{ selected.order.addedTitles.join(', ') }}.</p>
              <p v-if="selected.order.removedTitles?.length">Удалённые или перенесённые на сервере элементы исключены из порядка устройства: {{ selected.order.removedTitles.join(', ') }}.</p>
            </div>
          </template>
          <p v-else-if="previewDeleted" class="conflict-deleted">В этой версии элемент удалён. Выбор этой версии сохранит удаление.</p>
          <dl v-else-if="fields.length">
            <template v-for="(field, index) in fields" :key="index"><dt>{{ field.label }}</dt><dd>{{ field.value }}</dd></template>
          </dl>
          <p v-else>Содержимое версии отсутствует.</p>
        </section>
        <p v-if="error" class="conflict-error" role="alert">{{ error }}</p>
        <footer class="conflict-footer">
          <button type="button" class="conflict-later" @click="close">Решить позже</button>
          <button type="button" class="conflict-choose" :disabled="busy || !!selected.order?.error" @click="choose">{{ busy ? 'Сохраняем…' : selected.order ? (tab === 'local' ? 'Выбрать порядок устройства' : 'Выбрать порядок сервера') : (tab === 'local' ? 'Выбрать версию устройства' : 'Выбрать версию сервера') }}</button>
        </footer>
      </template>
    </div>
  </dialog>
</template>

<style scoped>
.conflict-trigger { align-self: flex-start; margin: 4px 20px 10px; padding: 9px 12px; border: 1px solid var(--warning-line); border-radius: 12px; background: var(--warning-surface); color: var(--warning); font: inherit; font-size: 13px; cursor: pointer; }
.conflict-dialog { width: min(620px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); padding: 0; border: 1px solid var(--line); border-radius: 20px; background: var(--surface, #fff); color: var(--text, #252938); box-shadow: 0 24px 80px #0004; }
.conflict-dialog::backdrop { background: #17203588; }
.conflict-dialog-inner { display: flex; flex-direction: column; gap: 14px; padding: 20px; }
.conflict-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.conflict-header h2 { margin: 0; font-size: 20px; }
.conflict-close { min-width: 44px; min-height: 44px; border: 0; border-radius: 10px; font-size: 26px; background: transparent; color: inherit; }
.conflict-hint { margin: 0; font-size: 14px; line-height: 1.5; opacity: .8; }
.conflict-list { display: flex; gap: 8px; flex-wrap: wrap; }
.conflict-list button { max-width: 100%; overflow-wrap: anywhere; border: 1px solid var(--line-strong); border-radius: 10px; background: transparent; color: inherit; padding: 8px 10px; }
.conflict-list .active { border-color: var(--accent); background: var(--accent-surface); color: var(--accent); }
.conflict-title { margin: 0; font-size: 17px; overflow-wrap: anywhere; }
.conflict-tabs { display: flex; gap: 6px; }
.conflict-tabs button { flex: 1; min-height: 44px; border: 1px solid var(--line-strong); border-radius: 10px; padding: 9px; font: inherit; font-size: 13px; color: inherit; background: transparent; }
.conflict-tabs [aria-selected="true"] { color: var(--accent); background: var(--accent-surface); border-color: var(--accent); }
.conflict-preview { min-height: 120px; max-height: 42dvh; overflow: auto; overscroll-behavior: contain; padding: 14px; border: 1px solid var(--line); border-radius: 12px; }
.conflict-preview dl, .conflict-preview p { margin: 0; }
.conflict-preview dt { margin-top: 16px; font-size: 12px; font-weight: 600; opacity: .7; }
.conflict-preview dt:first-child { margin-top: 0; }
.conflict-preview dd { margin: 5px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 14px; line-height: 1.5; }
.conflict-order { margin: 0; padding-left: 26px; font-size: 14px; line-height: 1.5; }
.conflict-order li { padding: 7px 0 7px 4px; overflow-wrap: anywhere; border-bottom: 1px solid var(--line); }
.conflict-order li:last-child { border-bottom: 0; }
.conflict-order-notes { font-size: 13px; line-height: 1.5; color: var(--muted); overflow-wrap: anywhere; }
.conflict-order-notes p { margin-top: 12px; }
.conflict-deleted, .conflict-error { color: var(--danger); line-height: 1.5; }
.conflict-error { margin: 0; font-size: 13px; }
.conflict-footer { display: flex; gap: 10px; justify-content: flex-end; }
.conflict-footer button { min-height: 44px; border-radius: 10px; padding: 10px 14px; font: inherit; font-size: 13px; cursor: pointer; }
.conflict-later { border: 1px solid var(--line-strong); background: transparent; color: inherit; }
.conflict-choose { border: 1px solid var(--primary); background: var(--primary); color: var(--on-primary); }
button:disabled { opacity: .55; cursor: wait; }
button:focus-visible, .conflict-preview:focus-visible { outline: 3px solid var(--accent); outline-offset: 2px; }
@media (max-width: 440px) { .conflict-dialog-inner { padding: 16px; } .conflict-footer { flex-direction: column-reverse; } .conflict-footer button { width: 100%; } }
</style>
