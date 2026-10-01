<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import { listEntityConflicts, resolveEntityConflict, type EntityConflict } from './conflicts'
import { getOfflineStore, subscribeSyncStatus } from './runtime'
import { conflictFields } from './conflictPresentation'
import type { OfflineEntity } from './types'

const conflicts = ref<EntityConflict[]>([])
const selected = ref<EntityConflict | null>(null)
const entities = ref<OfflineEntity[]>([])
const sides = ['local', 'server'] as const
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
const fields = computed(() => selected.value ? conflictFields(selected.value, entities.value) : [])
const localUnavailable = computed(() => selected.value?.serverDeleted && !selected.value.localDeleted && !selected.value.order)
function choiceLabel(side: 'local' | 'server') {
  if (selected.value?.order) return side === 'local' ? 'Оставить порядок устройства' : 'Оставить порядок сервера'
  if (side === 'local' ? selected.value?.localDeleted : selected.value?.serverDeleted) return 'Подтвердить удаление'
  return side === 'local' ? 'Оставить вариант устройства' : 'Оставить вариант сервера'
}
async function reload() {
  const current = ++generation
  try {
    const [items, records] = await Promise.all([listEntityConflicts(), getOfflineStore().then(store => store.listEntities())])
    if (current === generation) { conflicts.value = items; entities.value = records }
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось прочитать конфликты.' }
}
function pick(item: EntityConflict) {
  selected.value = item
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
async function choose(side: 'local' | 'server') {
  if (!selected.value || busy.value || selected.value.order?.error || (side === 'local' && localUnavailable.value)) return
  busy.value = true
  error.value = ''
  const resolvedKey = key(selected.value)
  try {
    await resolveEntityConflict(selected.value, side)
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
        <h2 id="conflict-heading">{{ selected?.order ? 'Какой порядок оставить?' : 'Какой вариант оставить?' }}</h2>
        <button type="button" class="conflict-close" aria-label="Закрыть без выбора" @click="close">×</button>
      </header>
      <p class="conflict-hint">{{ selected?.order ? 'Отличается порядок элементов.' : 'Показаны только отличия. Пока вы не выбрали, оба варианта сохранены.' }}</p>
      <div v-if="conflicts.length > 1" class="conflict-list" aria-label="Конфликтующие элементы">
        <button v-for="item in conflicts" :key="key(item)" type="button" :class="{ active: selected && key(selected) === key(item) }" :disabled="busy" @click="pick(item)">{{ item.title }}</button>
      </div>
      <template v-if="selected">
        <h3 class="conflict-title">{{ selected.order ? selected.title : `${entityLabel} · ${selected.title}` }}</h3>
        <p class="conflict-hint">{{ selected.order ? 'Сохранится только выбранный порядок. Содержимое не изменится.' : 'Выбранный вариант сохранится целиком.' }}</p>
        <p v-if="selected.order?.error" class="conflict-error" role="alert">{{ selected.order.error }} Закройте окно и откройте его снова.</p>
        <div :key="key(selected)" class="conflict-cards">
          <section v-for="side in sides" :key="side" class="conflict-card" :aria-labelledby="`conflict-${side}-heading`">
            <h4 :id="`conflict-${side}-heading`">{{ side === 'local' ? 'На этом устройстве' : 'На сервере' }}</h4>
            <div class="conflict-card-body">
              <template v-if="selected.order">
                <ol v-if="selected.order[side].length" class="conflict-order" :aria-label="side === 'local' ? 'Порядок на этом устройстве' : 'Порядок на сервере'">
                  <li v-for="item in selected.order[side]" :key="item.id">{{ item.title || 'Без названия' }}</li>
                </ol>
                <p v-else>В этом списке нет элементов.</p>
              </template>
              <dl v-else-if="fields.length">
                <template v-for="field in fields" :key="field.key">
                  <dt>{{ field.label }}</dt>
                  <dd :class="{ 'conflict-deleted': field.key === 'deleted' && field[side] === 'Удалена' }">
                    <details v-if="field.expandable && field[side] !== 'Удалено' && field[side] !== 'Не указано'">
                      <summary>{{ field.key === 'description' && selected.type === 'knowledge.node' ? 'Посмотреть текст' : 'Посмотреть описание' }}</summary>
                      <div class="conflict-text">{{ field[side] }}</div>
                    </details>
                    <template v-else>{{ field[side] }}</template>
                  </dd>
                </template>
              </dl>
              <p v-else>{{ selected.localDeleted && selected.serverDeleted ? 'Запись удалена.' : 'Содержимое совпадает.' }}</p>
            </div>
            <button type="button" class="conflict-choose" :disabled="busy || !!selected.order?.error || (side === 'local' && !!localUnavailable)" @click="choose(side)">{{ busy ? 'Сохраняем…' : choiceLabel(side) }}</button>
          </section>
        </div>
        <div v-if="selected.order && !selected.order.error" class="conflict-order-notes">
          <p v-if="selected.order.addedTitles?.length">Новые элементы с сервера добавлены в конец порядка устройства: {{ selected.order.addedTitles.join(', ') }}.</p>
          <p v-if="selected.order.removedTitles?.length">Удалённые или перенесённые на сервере элементы исключены из порядка устройства: {{ selected.order.removedTitles.join(', ') }}.</p>
        </div>
        <p v-if="localUnavailable" class="conflict-hint">На сервере запись удалена. Скопируйте нужный текст из карточки устройства перед подтверждением удаления.</p>
        <p v-if="error" class="conflict-error" role="alert">{{ error }}</p>
        <footer class="conflict-footer">
          <button type="button" class="conflict-later" @click="close">Решить позже</button>
        </footer>
      </template>
    </div>
  </dialog>
</template>

<style scoped>
.conflict-trigger { align-self: flex-start; margin: 4px 20px 10px; padding: 9px 12px; border: 1px solid var(--warning-line); border-radius: 12px; background: var(--warning-surface); color: var(--warning); font: inherit; font-size: 13px; cursor: pointer; }
.conflict-dialog { width: min(820px, calc(100vw - 24px)); box-sizing: border-box; max-height: calc(100dvh - 32px); padding: 0; border: 1px solid var(--line); border-radius: 20px; background: var(--surface, #fff); color: var(--text, #252938); box-shadow: 0 24px 80px #0004; }
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
.conflict-cards { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }
.conflict-card { min-width: 0; display: flex; flex-direction: column; gap: 14px; padding: 16px; border: 1px solid var(--line); border-radius: 14px; }
.conflict-card h4 { margin: 0; font-size: 15px; }
.conflict-card-body { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.conflict-card-body dl, .conflict-card-body p { margin: 0; }
.conflict-card-body dt { margin-top: 16px; font-size: 12px; font-weight: 600; opacity: .7; }
.conflict-card-body dt:first-child { margin-top: 0; }
.conflict-card-body dd { margin: 5px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 14px; line-height: 1.5; }
.conflict-card-body summary { color: var(--accent); cursor: pointer; min-height: 32px; }
.conflict-text { margin-top: 8px; max-height: 260px; overflow: auto; overscroll-behavior: contain; }
.conflict-order { margin: 0; padding-left: 26px; font-size: 14px; line-height: 1.5; }
.conflict-order li { padding: 7px 0 7px 4px; overflow-wrap: anywhere; border-bottom: 1px solid var(--line); }
.conflict-order li:last-child { border-bottom: 0; }
.conflict-order-notes { font-size: 13px; line-height: 1.5; color: var(--muted); overflow-wrap: anywhere; }
.conflict-order-notes p { margin-top: 12px; }
.conflict-deleted, .conflict-error { color: var(--danger); line-height: 1.5; }
.conflict-error { margin: 0; font-size: 13px; }
.conflict-footer { display: flex; gap: 10px; justify-content: flex-end; }
.conflict-footer button, .conflict-choose { min-height: 44px; border-radius: 10px; padding: 10px 14px; font: inherit; font-size: 13px; cursor: pointer; }
.conflict-later { border: 1px solid var(--line-strong); background: transparent; color: inherit; }
.conflict-choose { border: 1px solid var(--primary); background: var(--primary); color: var(--on-primary); }
button:disabled { opacity: .55; cursor: wait; }
button:focus-visible, summary:focus-visible { outline: 3px solid var(--accent); outline-offset: 2px; }
@media (max-width: 600px) { .conflict-cards { grid-template-columns: minmax(0, 1fr); } }
@media (max-width: 440px) { .conflict-dialog-inner { padding: 16px; } .conflict-footer { flex-direction: column-reverse; } .conflict-footer button { width: 100%; } }
</style>
