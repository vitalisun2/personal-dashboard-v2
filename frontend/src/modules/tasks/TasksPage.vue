<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getOfflineStore, saveOfflineMutation } from '../../offline/runtime'
import type { OfflineEntity, SyncOperation } from '../../offline/types'
import { startReorderDrag } from '../../shared/reorderDrag'
import ReorderHandle from '../../shared/ReorderHandle.vue'
import { readViewState, writeViewState } from '../../shared/uiViewState'
import { hasPendingGroupOrder, queueGroupOrder, readGroupOrder, type GroupOrderView } from './groupOrderOffline'

type Task = { id: string; title: string; description: string; projectId?: string; milestoneId?: string; featureId?: string; location: string; workStatus: string; sectionId?: string; archivedSectionName?: string; position: number; version: number }
type Section = { id: string; name: string; location: string; position: number; version: number; isBacklogVisible?: boolean }
type FeatureLabel = { id: string; title: string }
type MilestoneLabel = { id: string; title: string; features: FeatureLabel[] }
type ProjectLabel = { id: string; title: string; milestones: MilestoneLabel[] }
type MenuItem = { label: string; danger?: boolean; action: () => void }
type GroupView = { kind: 'plain'; key: string; title: string; section: Section; tasks: Task[] } | { kind: 'project'; key: string; title: string; projectId: string; tasks: Task[] }
type SwipeState = { key: string; pointerId: number; startX: number; startY: number; moved: boolean; long: boolean; timer: number }
type DragState = { kind: 'task' | 'section'; id: string; pointerId: number; row: HTMLElement; target: HTMLElement | null; place: 'before' | 'after' | 'inside' | ''; started: boolean; lifecycle: ReturnType<typeof startReorderDrag> }

const route = useRoute(), router = useRouter()
const savedView = readViewState<{ bucket: string; filter: string; archive: boolean; expanded: { Backlog: string[]; Сегодня: string[]; Archive?: string[] } }>('tasks', { bucket: 'Backlog', filter: 'all', archive: false, expanded: { Backlog: [], Сегодня: [] } })
const savedExpanded = savedView.expanded || { Backlog: [], Сегодня: [], Archive: [] }
const state = reactive({
  bucket: (savedView.bucket === 'Сегодня' ? 'Сегодня' : 'Backlog') as 'Backlog' | 'Сегодня', filter: ['all', 'New', 'InProgress', 'Done'].includes(savedView.filter) ? savedView.filter : 'all', archive: savedView.archive === true, orderMode: false,
  expanded: {
    Backlog: new Set<string>(Array.isArray(savedExpanded.Backlog) ? savedExpanded.Backlog.filter((key): key is string => typeof key === 'string') : []),
    Сегодня: new Set<string>(Array.isArray(savedExpanded['Сегодня']) ? savedExpanded['Сегодня'].filter((key): key is string => typeof key === 'string') : []),
    Archive: new Set<string>(Array.isArray(savedExpanded.Archive) ? savedExpanded.Archive.filter((key): key is string => typeof key === 'string') : []),
  },
  tasks: [] as Task[], allTasks: [] as Task[], sections: [] as Section[], projects: [] as ProjectLabel[],
  groupOrder: { version: 0, keys: [] } as GroupOrderView,
  detail: null as Task | null, busy: false, error: '',
  creating: false, createType: 'task' as 'task' | 'section', createListOpen: false,
  title: '', description: '', sectionId: '',
  renaming: null as null | { kind: 'task' | 'section'; id: string; value: string },
  detailEditing: '' as '' | 'title' | 'description',
  linkSheetOpen: false, linkProjectId: '', linkMilestoneId: '', linkFeatureId: '',
})
watch(() => ({ bucket: state.bucket, filter: state.filter, archive: state.archive, expanded: { Backlog: [...state.expanded.Backlog], Сегодня: [...state.expanded['Сегодня']], Archive: [...state.expanded.Archive] } }), value => writeViewState('tasks', value), { deep: true })
watch(() => route.path.split('/')[1], (section, previousSection) => {
  if (previousSection !== 'tasks' || section === 'tasks') return
  state.detailEditing = ''
  state.creating = false
  state.renaming = null
  state.linkSheetOpen = false
  state.orderMode = false
  closeMenu()
}, { flush: 'sync' })
const location = computed(() => state.archive ? 'Archived' : state.bucket === 'Сегодня' ? 'Today' : 'Backlog')
const filtered = computed(() => state.tasks.filter(task => !state.detail || task.id === state.detail.id).filter(task => state.filter === 'all' || statusName(task.workStatus) === state.filter))
const archiveTasks = computed(() => filtered.value)
const linkedGroups = computed(() => {
  const ids = [...new Set(filtered.value.filter(task => task.projectId).map(task => task.projectId!))]
  return ids.map(projectId => ({ projectId, title: state.projects.find(x => x.id === projectId)?.title || 'Проект', tasks: filtered.value.filter(task => task.projectId === projectId).sort((a, b) => a.position - b.position) }))
})
const groups = computed<GroupView[]>(() => {
  if (state.archive) return []
  const result: GroupView[] = []
  for (const section of state.sections.filter(section => state.bucket === 'Backlog' ? section.isBacklogVisible !== false : state.tasks.some(task => task.sectionId === section.id))) {
    const tasks = filtered.value.filter(item => item.sectionId === section.id)
    if (state.filter !== 'all' && !tasks.length) continue
    result.push({ kind: 'plain', key: `section:${section.id}`, title: section.name, section, tasks })
  }
  const ungrouped = filtered.value.filter(task => !task.projectId && !state.sections.some(section => section.id === task.sectionId))
  if (ungrouped.length) result.push({ kind: 'plain', key: 'section:local', title: 'Личное', section: { id: '', name: 'Личное', location: location.value, position: -1, version: 0 }, tasks: ungrouped.sort((a, b) => a.position - b.position) })
  for (const group of linkedGroups.value) {
    if (!group.tasks.length) continue
    result.push({ kind: 'project', key: `project:${group.projectId}`, title: group.title, projectId: group.projectId, tasks: group.tasks })
  }
  const positions = new Map(state.groupOrder.keys.map((key, index) => [key, index]))
  return result.sort((a, b) => (positions.get(a.key) ?? Number.MAX_SAFE_INTEGER) - (positions.get(b.key) ?? Number.MAX_SAFE_INTEGER))
})
const archiveGroups = computed<GroupView[]>(() => {
  const result: GroupView[] = []
  const sectionIds = [...new Set(archiveTasks.value.filter(task => task.sectionId && !task.projectId).map(task => task.sectionId!))]
  for (const id of sectionIds) {
    const section = state.sections.find(item => item.id === id)
    const tasks = archiveTasks.value.filter(task => task.sectionId === id)
    result.push({ kind: 'plain', key: `section:${id}`, title: section?.name || tasks[0]?.archivedSectionName || 'Личное', section: section || { id, name: tasks[0]?.archivedSectionName || 'Личное', location: 'Archived', position: 0, version: 0 }, tasks })
  }
  const unsectioned = archiveTasks.value.filter(task => !task.projectId && !task.sectionId)
  if (unsectioned.length) result.push({ kind: 'plain', key: 'section:local', title: 'Личное', section: { id: '', name: 'Личное', location: 'Archived', position: 0, version: 0 }, tasks: unsectioned })
  const projectIds = [...new Set(archiveTasks.value.filter(task => task.projectId).map(task => task.projectId!))]
  for (const projectId of projectIds) result.push({ kind: 'project', key: `project:${projectId}`, title: state.projects.find(item => item.id === projectId)?.title || 'Проект', projectId, tasks: archiveTasks.value.filter(task => task.projectId === projectId) })
  return result
})
const activeExpanded = computed(() => state.archive ? state.expanded.Archive : state.expanded[state.bucket])
const visibleGroups = computed(() => state.archive ? archiveGroups.value : groups.value)
const allOpen = computed(() => visibleGroups.value.length > 0 && visibleGroups.value.every(group => activeExpanded.value.has(group.key)))
const showEmpty = computed(() => state.archive ? archiveTasks.value.length === 0 && !state.busy : groups.value.length === 0 && !state.busy)
const emptyText = computed(() => state.busy ? 'Загружаем задачи…' : state.archive ? state.filter === 'all' ? 'Архив пока пуст.' : 'В архиве нет задач с таким статусом.' : 'Здесь пока нет задач.')
const detailOrigin = computed(() => state.detail ? originPath(state.detail) : '')
const selectedProject = computed(() => state.projects.find(x => x.id === state.linkProjectId))
const selectedMilestone = computed(() => selectedProject.value?.milestones.find(x => x.id === state.linkMilestoneId))
const selectedFeature = computed(() => selectedMilestone.value?.features.find(x => x.id === state.linkFeatureId))
const detailPlanningPath = computed(() => { const task = state.detail; if (!task?.featureId) return ''; const p = state.projects.find(x => x.id === task.projectId), m = p?.milestones.find(x => x.id === task.milestoneId), f = m?.features.find(x => x.id === task.featureId); return p && m && f ? `${p.title} › ${m.title} › ${f.title}` : '' })
const canMoveLinkToPlan = computed(() => Boolean(state.detail && String(state.detail.location).toLowerCase() === 'backlog' && selectedFeature.value))
const detailMoveLabel = computed(() => { const task = state.detail; if (!task) return ''; return isArchived(task) || isToday(task) || String(task.location).toLowerCase() === 'planned' ? 'В Backlog' : 'В Сегодня' })
const renamingKey = computed(() => state.renaming ? (state.renaming.kind === 'section' ? `section:${state.renaming.id}` : `task:${state.renaming.id}`) : '')
const renamingValue = computed({ get: () => state.renaming?.value ?? '', set: (value: string) => { if (state.renaming) state.renaming.value = value } })
const filters = [{ value: 'all', label: 'Все', dot: '' }, { value: 'New', label: 'Новые', dot: 'new' }, { value: 'InProgress', label: 'В работе', dot: 'work' }, { value: 'Done', label: 'Готово', dot: 'done' }]
const archiveFilters = filters
const archiveGroupLabel = 'Перенести все задачи группы в архив'

const rootEl = ref<HTMLElement | null>(null)
const groupsEl = ref<HTMLElement | null>(null)
const scrollEl = ref<HTMLElement | null>(null)
const menuEl = ref<HTMLElement | null>(null)
const titleInputEl = ref<HTMLInputElement | null>(null)
const descriptionInputEl = ref<HTMLTextAreaElement | null>(null)
const renameInputEl = ref<HTMLInputElement | null>(null)
const nameFieldEl = ref<HTMLInputElement | null>(null)
const confirmCancelEl = ref<HTMLButtonElement | null>(null)

let swipe: SwipeState | null = null
let drag: DragState | null = null
let suppressOpenUntil = 0
let renameDone = false

// ---------- confirm dialog ----------
const confirmBox = reactive({ open: false, title: '', body: '', confirmLabel: 'Подтвердить', onConfirm: null as (() => void) | null })
function askConfirm(options: { title: string; body?: string; confirmLabel?: string; onConfirm: () => void }) {
  closeMenu()
  confirmBox.title = options.title; confirmBox.body = options.body || ''; confirmBox.confirmLabel = options.confirmLabel || 'Подтвердить'; confirmBox.onConfirm = options.onConfirm; confirmBox.open = true
}
function closeConfirm() { confirmBox.open = false; confirmBox.onConfirm = null }
function runConfirm() { const callback = confirmBox.onConfirm; closeConfirm(); callback?.() }
watch(() => confirmBox.open, open => { if (open) nextTick(() => confirmCancelEl.value?.focus()) })

// ---------- context menu ----------
const menu = reactive({ open: false, kind: '' as 'task' | 'section' | 'project', key: '', items: [] as MenuItem[], x: 0, y: 0 })
const revealedKey = ref('')
function closeMenu() { if (!menu.open && !revealedKey.value) return; menu.open = false; menu.items = []; revealedKey.value = '' }
const isToday = (task: Task) => String(task.location).toLowerCase() === 'today'
const isArchived = (task: Task) => String(task.location).toLowerCase() === 'archived'
const isLinked = (task: Task) => Boolean(task.projectId && task.featureId)
const taskById = (id: string) => state.tasks.find(t => t.id === id)
function statusName(status: string) { return ({ '0': 'New', '1': 'InProgress', '2': 'Done', 'new': 'New', 'inProgress': 'InProgress', 'done': 'Done', 'in_progress': 'InProgress', 'active': 'InProgress', 'completed': 'Done', 'New': 'New', 'InProgress': 'InProgress', 'Done': 'Done' } as Record<string, string>)[String(status)] || String(status) }
function workState(status: string) { const s = statusName(status); return s === 'Done' ? 'completed' : s === 'InProgress' ? 'in_progress' : 'new' }
function workLabel(status: string) { return ({ 'new': 'Новая', 'in_progress': 'В работе', 'completed': 'Готово' } as Record<string, string>)[workState(status)] || 'Новая' }
function originPath(task: Task) {
  const base = task.projectId ? (state.projects.find(p => p.id === task.projectId)?.title || 'Проект') : ((isArchived(task) ? task.archivedSectionName : null) || state.sections.find(s => s.id === task.sectionId)?.name || 'Личное')
  return isArchived(task) ? `Архив · ${base}` : base
}
function openLinkSheet() { const task = state.detail; if (!task) return; state.linkProjectId = task.projectId || ''; state.linkMilestoneId = task.milestoneId || ''; state.linkFeatureId = task.featureId || ''; state.linkSheetOpen = true }
function chooseLinkProject(id: string) { state.linkProjectId = id; state.linkMilestoneId = ''; state.linkFeatureId = '' }
function chooseLinkMilestone(id: string) { state.linkMilestoneId = id; state.linkFeatureId = '' }
async function savePlanningLink(moveToPlanning: boolean) {
  const task = state.detail, feature = selectedFeature.value
  if (!task || !feature || !selectedProject.value || !selectedMilestone.value) return
  const ids = { projectId: selectedProject.value.id, milestoneId: selectedMilestone.value.id, featureId: feature.id }
  try {
    const updated = await request<Task>(`/${task.id}/planning-link`, { method: 'PUT', body: JSON.stringify({ expectedVersion: task.version, ...ids, moveToPlanning }) })
    state.detail = updated; state.tasks = state.tasks.map(x => x.id === updated.id ? updated : x); state.linkSheetOpen = false; await refresh();
    if (moveToPlanning) await router.push(`/planning/projects/${ids.projectId}/milestones/${ids.milestoneId}/features/${ids.featureId}`)
  } catch (error) {
    if (!(error instanceof LocalMutation)) { state.error = (error as Error).message; return }
    const local: Task = { ...task, ...ids, sectionId: undefined, ...(moveToPlanning ? { location: 'planned', workStatus: 'new' } : {}), version: task.version + 1 }
    const payload = { operation: 'update', kind: 'task', id: task.id, expectedVersion: task.version, ...(moveToPlanning ? { placement: 'planned' } : {}), workStatus: statusName(task.workStatus) === 'InProgress' ? 'inProgress' : statusName(task.workStatus).toLowerCase(), sectionId: null, planning: ids }
    await queueTask('tasks.task', task.id, task.version, payload, false, local)
    state.detail = local; state.tasks = state.tasks.map(x => x.id === local.id ? local : x); state.linkSheetOpen = false
    state.error = ''
    if (moveToPlanning) { state.detail = null; await router.push(`/planning/projects/${ids.projectId}/milestones/${ids.milestoneId}/features/${ids.featureId}`) }
    else await refresh()
  }
}
function advanceLabel(task: Task) { const s = statusName(task.workStatus); return s === 'Done' ? 'Отметить «Новая»' : s === 'InProgress' ? 'Отметить «Готово»' : 'Отметить «В работе»' }
function menuItemsFor(kind: 'task' | 'section' | 'project', id: string): MenuItem[] {
  if (kind === 'task') {
    const task = taskById(id); if (!task) return []
    const items: MenuItem[] = []
    if (isToday(task)) items.push({ label: advanceLabel(task), action: () => { void advanceTask(task) } })
    items.push({
      label: isArchived(task) ? 'Вернуть в Backlog' : String(task.location).toLowerCase() === 'backlog' ? 'Перенести в Сегодня' : 'Вернуть в Backlog',
      action: () => { if (isArchived(task)) { void mutate(task, 'restore') } else void moveTaskVia(task, String(task.location).toLowerCase() === 'backlog' ? 'today' : 'backlog') },
    })
    if (String(task.location).toLowerCase() === 'backlog' && isLinked(task)) items.push({ label: 'Вернуть в план', action: () => { void mutate(task, 'planning') } })
    items.push({ label: 'Переименовать', action: () => startRename('task', task.id) })
    if (!isArchived(task)) items.push({ label: 'Убрать в архив', danger: true, action: () => archiveTask(task) })
    else items.push({ label: 'Удалить навсегда', danger: true, action: () => deleteArchivedTask(task) })
    return items
  }
  if (kind === 'section') {
    const section = state.sections.find(s => s.id === id)
    if (state.archive) {
      if (!section) return []
      const hasActiveTasks = state.allTasks.some(task => task.sectionId === id && (String(task.location).toLowerCase() === 'backlog' || String(task.location).toLowerCase() === 'today'))
      return hasActiveTasks
        ? [{ label: 'Удалить архивные задачи', danger: true, action: () => deleteArchivedSectionTasksFlow(section) }]
        : [{ label: 'Удалить раздел и задачи архива', danger: true, action: () => deleteArchivedSectionFlow(section) }]
    }
    const tasks = id === 'local'
      ? state.tasks.filter(task => !task.projectId && !state.sections.some(item => item.id === task.sectionId))
      : state.tasks.filter(t => t.sectionId === id)
    const items: MenuItem[] = []
    if (state.bucket === 'Сегодня') {
      const completed = tasks.filter(task => statusName(task.workStatus) === 'Done')
      if (tasks.length) items.push({ label: 'Вернуть все задачи группы в Backlog', action: () => { void moveGroupTasks(tasks, 'backlog') } })
      if (tasks.length) items.push({ label: archiveGroupLabel, action: () => archiveGroupTasks(section?.name || 'Личное', tasks) })
      if (completed.length) items.push({ label: 'Переместить готовые в архив', action: () => archiveCompletedTasks(section?.name || 'Личное', completed) })
    }
    if (!section) {
      if (state.bucket === 'Backlog' && tasks.length) {
        items.push({ label: 'Перенести все задачи группы в Сегодня', action: () => { void moveGroupTasks(tasks, 'today') } })
        items.push({ label: archiveGroupLabel, action: () => archiveGroupTasks('Личное', tasks) })
      }
      return items
    }
    if (state.bucket === 'Backlog') {
      items.push({ label: 'Переименовать', action: () => startRename('section', section.id) })
      if (tasks.length) items.push({ label: 'Перенести все задачи группы в Сегодня', action: () => { void moveGroupTasks(tasks, 'today') } })
      const anySectionTasks = state.allTasks.some(task => task.sectionId === section.id)
      const label = tasks.length ? archiveGroupLabel : anySectionTasks ? 'Убрать из Backlog' : 'Удалить раздел'
      items.push({ label, danger: true, action: () => deleteSectionFlow(section, tasks, anySectionTasks) })
    }
    return items
  }
  if (state.archive) {
    return [{ label: 'Удалить архивные задачи', danger: true, action: () => deleteArchivedProjectGroupFlow(id) }]
  }
  const tasks = state.tasks.filter(t => t.projectId === id)
  const items: MenuItem[] = []
  if (state.bucket === 'Сегодня') {
    const completed = tasks.filter(task => statusName(task.workStatus) === 'Done')
    if (tasks.length) items.push({ label: 'Вернуть все задачи группы в Backlog', action: () => { void moveGroupTasks(tasks, 'backlog') } })
    if (tasks.length) items.push({ label: archiveGroupLabel, action: () => archiveGroupTasks(state.projects.find(project => project.id === id)?.title || 'Проект', tasks) })
    if (completed.length) items.push({ label: 'Переместить готовые в архив', action: () => archiveCompletedTasks(state.projects.find(project => project.id === id)?.title || 'Проект', completed) })
  }
  if (state.bucket === 'Backlog' && tasks.length) {
    items.push({ label: 'Перенести все задачи группы в Сегодня', action: () => { void moveGroupTasks(tasks, 'today') } })
    items.push({ label: archiveGroupLabel, action: () => deleteProjectGroupFlow(id, tasks) })
  }
  return items
}
function openMenuAt(key: string, point: { x: number; y: number } | null) {
  if (state.orderMode) return
  const kind = key.startsWith('task:') ? 'task' : key.startsWith('section:') ? 'section' : 'project'
  const id = kind === 'task' ? key.slice(5) : kind === 'section' ? key.slice(8) : key.slice(8)
  const items = menuItemsFor(kind, id)
  if (!items.length) return
  const wrap = groupsEl.value?.querySelector<HTMLElement>(`[data-reveal-key="${CSS.escape(key)}"]`)
  if (!wrap) return
  const rect = wrap.getBoundingClientRect()
  closeMenu()
  revealedKey.value = key
  menu.kind = kind; menu.key = key; menu.items = items; menu.x = -9999; menu.y = -9999; menu.open = true
  nextTick(() => positionMenu(rect, point))
  if (!point) suppressOpenUntil = Date.now() + 350
}
function positionMenu(anchorRect: DOMRect, point: { x: number; y: number } | null) {
  const el = menuEl.value, root = rootEl.value
  if (!el || !root) return
  const shell = root.getBoundingClientRect()
  const leftLimit = Math.max(shell.left + 8, 8)
  const rightLimit = Math.min(shell.right - 8, window.innerWidth - 8)
  const topLimit = Math.max(shell.top + 8, 8)
  const bottomLimit = Math.min(shell.bottom - 8, window.innerHeight - 8)
  const width = el.offsetWidth, height = el.offsetHeight
  let x, y
  if (point) {
    x = point.x + 8; y = point.y + 8
    if (x + width > rightLimit) x = point.x - width - 8
    if (y + height > bottomLimit) y = point.y - height - 8
  } else {
    x = anchorRect.right - width
    y = anchorRect.top - height - 6
    if (y < topLimit) y = anchorRect.bottom + 6
    if (y + height > bottomLimit) y = anchorRect.top - height - 6
  }
  x = Math.max(leftLimit, Math.min(x, rightLimit - width))
  y = Math.max(topLimit, Math.min(y, bottomLimit - height))
  el.style.left = `${x}px`; el.style.top = `${y}px`
  el.style.setProperty('--menu-enter-y', y < anchorRect.top ? '6px' : '-6px')
  el.classList.remove('opening'); void el.offsetWidth; el.classList.add('opening')
}
function triggerMenu(key: string) {
  if (state.orderMode) return
  if (menu.open && menu.key === key) closeMenu()
  else openMenuAt(key, null)
}
function rowContextMenu(event: MouseEvent, key: string) {
  event.preventDefault()
  if (state.orderMode || menu.open) return
  openMenuAt(key, { x: event.clientX, y: event.clientY })
}
function onDocPointerDown(event: PointerEvent) {
  if (!menu.open) return
  const target = event.target as HTMLElement | null
  if (menuEl.value?.contains(target) || target?.closest('.row-menu-trigger')) return
  closeMenu()
}
function onDocKeyDown(event: KeyboardEvent) {
  if (event.key !== 'Escape') return
  if (menu.open) { event.preventDefault(); closeMenu() }
  else if (confirmBox.open) { event.preventDefault(); closeConfirm() }
}
function suppressCapture(event: MouseEvent) {
  if (Date.now() < suppressOpenUntil && !(event.target as HTMLElement).closest('.row-context-menu')) { event.preventDefault(); event.stopPropagation() }
}
function onTaskOpenClick(task: Task) {
  if (state.orderMode) return
  if (revealedKey.value) { closeMenu(); return }
  state.orderMode = false
  openTask(task)
}

// ---------- gestures (long-press / swipe) ----------
function rowPointerDown(event: PointerEvent, key: string) {
  if (state.orderMode || event.button > 0) return
  if ((event.target as HTMLElement).closest('.handle,.row-menu-trigger,.task-inline-input')) return
  if (swipe?.timer) window.clearTimeout(swipe.timer)
  const current: SwipeState = { key, pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, moved: false, long: false, timer: 0 }
  swipe = current
  if (event.pointerType === 'touch' || event.pointerType === 'pen') {
    current.timer = window.setTimeout(() => {
      if (swipe !== current || current.moved) return
      current.long = true
      openMenuAt(key, null)
    }, 480)
  }
}
function rowPointerMove(event: PointerEvent) {
  if (!swipe || swipe.pointerId !== event.pointerId) return
  const dx = event.clientX - swipe.startX, dy = event.clientY - swipe.startY
  if (Math.hypot(dx, dy) > 8) { swipe.moved = true; window.clearTimeout(swipe.timer) }
  if (Math.abs(dy) > Math.abs(dx) && Math.abs(dy) > 10) { swipe = null; return }
  if (dx < -12 && Math.abs(dx) > Math.abs(dy) * 1.1) event.preventDefault()
}
function rowPointerUp(event: PointerEvent) {
  if (!swipe || swipe.pointerId !== event.pointerId) return
  const current = swipe
  window.clearTimeout(current.timer); swipe = null
  if (current.long) return
  const dx = event.clientX - current.startX, dy = event.clientY - current.startY
  if (Math.abs(dx) < Math.abs(dy) * 1.2) return
  if (dx <= -56) { suppressOpenUntil = Date.now() + 320; openMenuAt(current.key, null) }
  else if (dx >= 45 && menu.open) { suppressOpenUntil = Date.now() + 320; closeMenu() }
}
function rowPointerCancel() { if (swipe) { window.clearTimeout(swipe.timer); swipe = null } }

// ---------- navigation / actions ----------
function openTask(task: Task) { if (!state.orderMode && Date.now() >= suppressOpenUntil) { closeMenu(); state.detailEditing = ''; void router.push(`/tasks/${task.id}`) } }
function closeDetail() { state.detailEditing = ''; void router.push('/tasks') }
function selectBucket(bucket: 'Backlog' | 'Сегодня') { closeMenu(); state.bucket = bucket; state.archive = false; state.orderMode = false; state.filter = 'all'; state.detail = null; state.title = ''; state.description = ''; void router.replace('/tasks').then(refresh) }
function openArchive() { closeMenu(); state.orderMode = false; state.filter = 'all'; state.archive = true; if (route.path !== '/tasks') { void router.replace('/tasks') } else { void refresh() } }
function toggleAllSections() {
  const keys = visibleGroups.value.map(g => g.key)
  const open = keys.length > 0 && keys.every(key => activeExpanded.value.has(key))
  if (state.archive) state.expanded.Archive = new Set(open ? [] : keys)
  else state.expanded[state.bucket] = new Set(open ? [] : keys)
}
function toggleSectionFor(key: string) {
  if (state.orderMode) return
  if (revealedKey.value) { closeMenu(); return }
  const open = activeExpanded.value
  open.has(key) ? open.delete(key) : open.add(key)
}
function isExpanded(key: string) { return activeExpanded.value.has(key) }
function toggleOrderMode() { closeMenu(); swipe = null; clearGhost(); clearDragMarks(); drag = null; state.orderMode = !state.orderMode }
async function moveTaskVia(task: Task, target: 'today' | 'backlog') {
  return mutate(task, target)

}
async function moveGroupTasks(tasks: Task[], target: 'today' | 'backlog') {
  for (const task of tasks) if (!await moveTaskVia(task, target)) break
}
async function advanceTask(task: Task) {
  const s = statusName(task.workStatus)
  const next = s === 'Done' ? 'new' : s === 'InProgress' ? 'done' : 'inProgress'
  await mutate(task, 'status', 'PUT', { expectedVersion: task.version, status: next })

}
function archiveTask(task: Task) {
  askConfirm({ title: 'Убрать задачу в архив?', body: `«${task.title}» можно будет найти в архиве.`, confirmLabel: 'В архив', onConfirm: () => { void mutate(task, 'archive') } })
}
function archiveCompletedTasks(sectionName: string, tasks: Task[]) {
  askConfirm({
    title: `Переместить готовые задачи раздела «${sectionName}» в архив?`,
    body: 'Выполненные задачи можно будет найти в архиве. Раздел и незавершённые задачи останутся на месте.',
    confirmLabel: 'В архив',
    onConfirm: () => { void (async () => { for (const task of tasks) if (!await mutate(task, 'archive')) break })() },
  })
}
function archiveGroupTasks(groupName: string, tasks: Task[]) {
  const source = state.bucket, other = source === 'Сегодня' ? 'Backlog' : 'Сегодня'
  askConfirm({
    title: `Перенести задачи группы «${groupName}» в архив?`,
    body: `${tasks.length} задач из ${source} перейдут в архив. Задачи этой группы в ${other} останутся на месте.`,
    confirmLabel: 'В архив',
    onConfirm: () => { void (async () => { for (const task of tasks) if (!await mutate(task, 'archive')) break })() },
  })
}
function deleteSectionFlow(section: Section, tasks: Task[], hasRelatedTasks: boolean) {
  if (!tasks.length) { void deleteSection(section, hasRelatedTasks); return }
  askConfirm({
    title: `Переместить задачи раздела «${section.name}» в архив?`,
    body: `${tasks.length} задач из Backlog перейдут в архив. Задачи раздела в Сегодня не изменятся.`,
    confirmLabel: 'В архив',
    onConfirm: () => { void deleteSection(section, hasRelatedTasks) },
  })
}
function deleteArchivedSectionFlow(section: Section) {
  const tasks = state.allTasks.filter(task => task.sectionId === section.id && isArchived(task))
  askConfirm({ title: `Удалить раздел «${section.name}» и задачи в нём навсегда?`, body: `${tasks.length} архивных задач будут удалены.`, confirmLabel: 'Удалить навсегда', onConfirm: () => { void deleteArchivedSection(section, tasks) } })
}
function deleteArchivedSectionTasksFlow(section: Section) {
  const tasks = state.allTasks.filter(task => task.sectionId === section.id && isArchived(task))
  askConfirm({ title: `Удалить архивные задачи раздела «${section.name}» навсегда?`, body: `${tasks.length} архивных задач будут удалены. Задачи в Сегодня и Backlog останутся на месте.`, confirmLabel: 'Удалить навсегда', onConfirm: () => { void (async () => { for (const task of tasks) await removeArchivedTask(task) })() } })
}
function deleteArchivedProjectGroupFlow(projectId: string) {
  const title = state.projects.find(project => project.id === projectId)?.title || 'Проект'
  const tasks = state.allTasks.filter(task => task.projectId === projectId && isArchived(task))
  askConfirm({ title: `Удалить архивные задачи проекта «${title}» навсегда?`, body: `${tasks.length} архивных задач будут удалены.`, confirmLabel: 'Удалить навсегда', onConfirm: () => { void (async () => { for (const task of tasks) await removeArchivedTask(task) })() } })
}
function deleteProjectGroupFlow(projectId: string, tasks: Task[]) {
  const title = state.projects.find(p => p.id === projectId)?.title || 'Проект'
  askConfirm({
    title: `Переместить задачи проекта «${title}» в архив?`,
    body: `${tasks.length} задач будут перемещены в архив.`,
    confirmLabel: 'В архив',
    onConfirm: () => { void (async () => { for (const task of tasks) if (!await mutate(task, 'archive')) break })() },
  })
}

// ---------- inline rename ----------
function startRename(kind: 'task' | 'section', id: string) {
  const current = kind === 'task' ? taskById(id) : state.sections.find(s => s.id === id)
  if (!current) return
  closeMenu()
  renameDone = false
  state.renaming = { kind, id, value: kind === 'task' ? (current as Task).title : (current as Section).name }
  nextTick(() => { const el = renameInputEl.value; if (el) { el.focus(); el.select() } })
}
async function commitRename(save: boolean) {
  const renaming = state.renaming
  if (!renaming || renameDone) return
  renameDone = true
  const value = renaming.value.trim()
  state.renaming = null
  if (!save || !value) return
  if (renaming.kind === 'task') { const task = taskById(renaming.id); if (task) await renameTask(task, value) }
  else { const section = state.sections.find(s => s.id === renaming.id); if (section) await renameSection(section, value) }
}
async function renameTask(task: Task, title: string) {
  try { await request(`/${task.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: task.version, title, description: task.description }) }); await refresh() }
  catch (error) {
    if (error instanceof LocalMutation) {
      const local = { ...task, title, version: task.version + 1 }
      await queueTask('tasks.task', task.id, task.version, { operation: 'update', kind: 'task', id: task.id, expectedVersion: task.version, title, description: task.description }, false, local)
      state.tasks = state.tasks.map(x => x.id === task.id ? local : x)
      if (state.detail?.id === task.id) state.detail = local
      await cacheRows('tasks.task', state.tasks, true)
      state.error = ''; return
    }
    state.error = (error as Error).message
  }
}
async function renameSection(section: Section, name: string) {
  if (!name || name === section.name) return
  try { await request(`/sections/${section.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: section.version, name }) }); await refresh() }
  catch (error) {
    if (error instanceof LocalMutation) {
      const local = { ...section, name, version: section.version + 1 }
      await queueTask('tasks.section', section.id, section.version, { operation: 'update', kind: 'section', id: section.id, expectedVersion: section.version, title: name, bucket: section.location }, false, local)
      state.sections = state.sections.map(x => x.id === section.id ? local : x); state.error = ''; return
    }
    state.error = (error as Error).message
  }
}
async function deleteSection(section: Section, hasRelatedTasks: boolean) {
  try { await request(`/sections/${section.id}`, { method: 'DELETE', body: JSON.stringify({ expectedVersion: section.version }) }); await refresh() }
  catch (error) {
    if (error instanceof LocalMutation) {
      await queueTask('tasks.section', section.id, section.version, { operation: 'delete', kind: 'section', id: section.id, bucket: 'backlog' }, true)
      const archived = state.allTasks.filter(task => task.sectionId === section.id && String(task.location).toLowerCase() === 'backlog').map(task => ({ ...task, location: 'archived', archivedSectionName: section.name, version: task.version + 1 }))
      state.allTasks = [...state.allTasks.filter(task => task.sectionId !== section.id || String(task.location).toLowerCase() !== 'backlog'), ...archived]
      state.tasks = state.tasks.filter(task => task.sectionId !== section.id || String(task.location).toLowerCase() !== 'backlog')
      const hidden = { ...section, isBacklogVisible: false, version: section.version + 1 }
      state.sections = hasRelatedTasks ? state.sections.map(item => item.id === section.id ? hidden : item) : state.sections.filter(item => item.id !== section.id)
      await cacheRows('tasks.task', archived, true)
      if (hasRelatedTasks) {
        const store = await getOfflineStore(), cachedSection = await store.getEntity('tasks.section.view', section.id)
        if (cachedSection) await store.putEntity({ ...cachedSection, payload: hidden, deleted: false, updatedAt: new Date().toISOString() })
      } else await cacheRows('tasks.section', state.sections, true)
      state.error = ''; return
    }
    state.error = (error as Error).message
  }
}
async function deleteArchivedSection(section: Section, archivedTasks: Task[]) {
  try { await request(`/sections/${section.id}/archive`, { method: 'DELETE', body: JSON.stringify({ expectedVersion: section.version }) }); await refresh() }
  catch (error) {
    if (error instanceof LocalMutation) {
      await queueTask('tasks.section', section.id, section.version, { operation: 'delete', kind: 'section', id: section.id, bucket: 'archive' }, true)
      const deletedIds = new Set(archivedTasks.map(task => task.id))
      state.allTasks = state.allTasks.filter(task => !deletedIds.has(task.id))
      state.tasks = state.tasks.filter(task => !deletedIds.has(task.id))
      state.sections = state.sections.filter(item => item.id !== section.id)
      const store = await getOfflineStore(), cachedSection = await store.getEntity('tasks.section.view', section.id), now = new Date().toISOString()
      await Promise.all(archivedTasks.map(task => store.putEntity({ type: 'tasks.task.view', id: task.id, version: task.version + 1, payload: null, deleted: true, updatedAt: now })))
      if (cachedSection) await store.putEntity({ ...cachedSection, deleted: true, updatedAt: new Date().toISOString() })
      state.error = ''; return
    }
    state.error = (error as Error).message
  }
}

// ---------- create sheet ----------
function openCreateSheet() {
  if (state.archive || state.bucket !== 'Backlog') return
  closeMenu(); closeConfirm()
  state.createType = 'task'; state.title = ''; state.description = ''
  const visibleSections = state.sections.filter(section => section.isBacklogVisible !== false)
  if (!visibleSections.some(s => s.id === state.sectionId)) state.sectionId = visibleSections[0]?.id || ''
  state.createListOpen = false; state.creating = true
  nextTick(() => nameFieldEl.value?.focus())
}
function closeCreateSheet() { state.creating = false; state.createListOpen = false }
async function submitCreate() {
  if (state.archive || state.bucket !== 'Backlog') { closeCreateSheet(); return }
  if (!state.title.trim()) return
  try {
    if (state.createType === 'section') {
      const created = await createSection()
      if (created) { closeCreateSheet(); }
      return
    }
    const created = await createTask()
    if (created) { closeCreateSheet(); void router.push(`/tasks/${created.id}`) }
  } catch (error) { state.error = (error as Error).message }
}
async function createTask(): Promise<Task | null> {
  if (!state.title.trim() || state.archive || state.bucket !== 'Backlog') return null
  let created: Task
  try { created = await request<Task>('', { method: 'POST', body: JSON.stringify({ title: state.title, description: state.description, projectId: null, milestoneId: null, featureId: null, sectionId: state.sectionId || null }) }) }
  catch (error) {
    if (!(error instanceof LocalMutation)) throw error
    const id = newId(), sectionId = state.sectionId || state.sections.find(section => section.isBacklogVisible !== false)?.id || null
    const payload = { operation: 'create', kind: 'task', id, title: state.title.trim(), description: state.description, placement: 'backlog', workStatus: 'new', sectionId }
    const local: Task = { id, title: state.title.trim(), description: state.description, location: 'backlog', workStatus: 'new', sectionId: sectionId || undefined, position: state.tasks.length, version: 1 }
    await queueTask('tasks.task', id, null, payload, false, local)
    created = { id, title: state.title.trim(), description: state.description, location: 'backlog', workStatus: 'new', sectionId: sectionId || undefined, position: state.tasks.length, version: 1 }
    state.tasks.push(created); state.allTasks.push(created); await cacheRows('tasks.task', state.tasks, true); state.error = ''
  }
  state.title = ''; state.description = ''; state.creating = false
  if (created.sectionId) state.expanded.Backlog.add(`section:${created.sectionId}`)
  if (created.id && !state.allTasks.some(task => task.id === created.id)) state.allTasks.push(created)
  if (navigator.onLine) await refresh()
  return created
}
async function createSection(): Promise<Section | null> {
  if (!state.title.trim() || state.archive || state.bucket !== 'Backlog') return null
  let created: Section
  try { created = await request<Section>('/sections', { method: 'POST', body: JSON.stringify({ name: state.title, location: 'backlog' }) }) }
  catch (error) {
    if (!(error instanceof LocalMutation)) { state.error = (error as Error).message; return null }
    const id = newId(), bucket = 'backlog'
    const payload = { operation: 'create', kind: 'section', id, title: state.title.trim(), bucket, position: state.sections.length }
    const local: Section = { id, name: state.title.trim(), location: bucket, isBacklogVisible: true, position: state.sections.length, version: 1 }
    await queueTask('tasks.section', id, null, payload, false, local)
    created = local
    state.sections.push(created); await cacheRows('tasks.section', state.sections, true); state.error = ''
  }
  state.title = ''; state.creating = false; if (navigator.onLine) await refresh(); state.sectionId = created.id
  state.expanded[state.bucket].add(`section:${created.id}`)
  return created
}

// ---------- detail inline editing ----------
function startTitleEdit() {
  if (!state.detail || state.detailEditing) return
  finishDescEdit()
  state.detailEditing = 'title'
  nextTick(() => { const el = titleInputEl.value; if (el) { el.focus(); el.select() } })
}
function finishTitleEdit(save = true) {
  if (state.detailEditing !== 'title') return
  const task = state.detail
  if (!task) { state.detailEditing = ''; return }
  if (save) { const title = state.title.trim(); if (title && title !== task.title) { state.title = title; void saveDetail() } else { state.title = task.title; state.description = task.description } }
  else { state.title = task.title; state.description = task.description }
  state.detailEditing = ''
}
function startDescEdit() {
  if (!state.detail || state.detailEditing) return
  finishTitleEdit()
  state.detailEditing = 'description'
  nextTick(() => descriptionInputEl.value?.focus())
}
function finishDescEdit(save = true) {
  if (state.detailEditing !== 'description') return
  const task = state.detail
  if (!task) { state.detailEditing = ''; return }
  if (save) { if (state.description !== task.description) void saveDetail(); else state.title = task.title }
  else { state.title = task.title; state.description = task.description }
  state.detailEditing = ''
}
function detailMove() {
  const task = state.detail; if (!task) return
  if (isArchived(task)) void mutate(task, 'restore')
  else void moveTaskVia(task, isToday(task) || String(task.location).toLowerCase() === 'planned' ? 'backlog' : 'today')
}
function deleteArchivedTask(task: Task) {
  if (!isArchived(task)) return
  askConfirm({ title: 'Удалить задачу навсегда?', body: `«${task.title}» исчезнет из архива и расчёта задач фичи. Восстановить её будет нельзя.`, confirmLabel: 'Удалить навсегда', onConfirm: () => { void removeArchivedTask(task) } })
}
async function removeArchivedTask(task: Task) {
  try {
    await request(`/${task.id}`, { method: 'DELETE', body: JSON.stringify({ expectedVersion: task.version }) })
    await (await getOfflineStore()).putEntity({ type: 'tasks.task.view', id: task.id, version: task.version + 1, payload: null, deleted: true, updatedAt: new Date().toISOString() })
  } catch (error) {
    if (!(error instanceof LocalMutation)) { state.error = (error as Error).message; return }
    try {
      await queueTask('tasks.task', task.id, task.version, { operation: 'delete', kind: 'task', id: task.id }, true)
      state.error = ''
    } catch (saveError) { state.error = (saveError as Error).message; return }
  }
  state.tasks = state.tasks.filter(item => item.id !== task.id)
  state.allTasks = state.allTasks.filter(item => item.id !== task.id)
  if (state.detail?.id === task.id) {
    state.detail = null; state.archive = true
    await router.replace('/tasks')
  }

}

// ---------- drag & reorder (pointer-based, order mode) ----------
function validTaskDrop(moved: Task, target: Task | { projectId?: string }, place: 'row' | 'inside'): boolean {
  if (!moved) return false
  if (isLinked(moved)) {
    if (place === 'inside') return (target as { projectId?: string }).projectId === moved.projectId
    const t = target as Task
    return isLinked(t) && t.projectId === moved.projectId
  }
  if (place === 'inside') return !(target as { projectId?: string }).projectId
  return !isLinked(target as Task)
}
function onGroupsPointerDown(event: PointerEvent) {
  if (event.button !== 0 || state.archive || !state.orderMode) return
  const handle = (event.target as HTMLElement).closest<HTMLElement>('[data-drag-kind]')
  if (!handle) return
  event.preventDefault()
  closeMenu()
  const kind = handle.dataset.dragKind === 'section' ? 'section' : 'task'
  const id = handle.dataset.dragId || ''
  const row = kind === 'task' ? handle.closest<HTMLElement>('[data-task-id]') : handle.closest<HTMLElement>('[data-section-row-key]')
  if (!row) return
  drag = { kind, id, pointerId: event.pointerId, row, target: null, place: '', started: false, lifecycle: startReorderDrag(event, row, () => scrollEl.value?.getBoundingClientRect()) }
}
function onGroupsPointerMove(event: PointerEvent) {
  if (!drag || drag.pointerId !== event.pointerId) return
  if (drag.lifecycle.update(event)) {
    if (!drag.started) {
      drag.started = true
      drag.row.classList.add('task-drag-source')
    }
  }
  if (!drag.started) return
  event.preventDefault()
  clearDragMarks()
  drag.target = null; drag.place = ''
  const hit = document.elementFromPoint(event.clientX, event.clientY)
  if (!hit) return
  if (drag.kind === 'task') {
    const taskRow = hit.closest<HTMLElement>('[data-task-id]')
    if (taskRow && taskRow.dataset.taskId !== drag.id) {
      const target = taskById(taskRow.dataset.taskId || ''), moved = taskById(drag.id)
      if (!target || !moved || !validTaskDrop(moved, target, 'row')) return
      const rect = taskRow.getBoundingClientRect()
      drag.target = taskRow; drag.place = event.clientY < rect.top + rect.height / 2 ? 'before' : 'after'
      taskRow.classList.add(drag.place === 'before' ? 'task-drop-before' : 'task-drop-after')
      return
    }
    const sectionRow = hit.closest<HTMLElement>('[data-section-row-key]')
    if (sectionRow && sectionRow.dataset.dragKey !== `project:${drag.id}`) {
      const moved = taskById(drag.id)
      if (!moved || !validTaskDrop(moved, { projectId: sectionRow.dataset.sectionProject || undefined }, 'inside')) return
      drag.target = sectionRow; drag.place = 'inside'
      sectionRow.classList.add('task-drop-inside')
    }
    return
  }
  const sectionRow = hit.closest<HTMLElement>('[data-section-row-key]')
  if (!sectionRow || sectionRow.dataset.dragKey === drag.id) return
  const rect = sectionRow.getBoundingClientRect()
  drag.target = sectionRow; drag.place = event.clientY < rect.top + rect.height / 2 ? 'before' : 'after'
  sectionRow.classList.add(drag.place === 'before' ? 'task-drop-before' : 'task-drop-after')
}
async function finishDrag(event: PointerEvent) {
  if (!drag || drag.pointerId !== event.pointerId) return
  const current = drag
  clearGhost(); clearDragMarks(); drag = null
  if (!current.started || !current.target || !current.place) return
  if (current.kind === 'section') {
    const targetKey = current.target.dataset.dragKey || ''
    if (!targetKey) return
    await dropGroup(current.id, targetKey, current.place === 'after')
    return
  }
  const moved = taskById(current.id)
  if (!moved) return
  if (current.place === 'inside') {
    const key = current.target.dataset.dragKey || ''
    if (key.startsWith('project:')) {
      const projectId = key.slice(8)
      const items = state.tasks.filter(t => t.projectId === projectId).sort((a, b) => a.position - b.position)
      const last = items[items.length - 1]
      if (last && last.id !== moved.id) await reorderProjectTasks(projectId, moved.id, last.id)
    } else {
      const sectionId = current.target.dataset.dragId || ''
      if (sectionId && sectionId !== (moved.sectionId || '')) await moveTaskSection(moved, sectionId)
    }
    return
  }
  const target = taskById(current.target.dataset.taskId || '')
  if (!target) return
  if (isLinked(moved) && isLinked(target) && target.projectId === moved.projectId) { await reorderProjectTasks(moved.projectId!, moved.id, target.id); return }
  if (isLinked(moved) || isLinked(target)) return
  const targetSectionId = target.sectionId || ''
  if ((moved.sectionId || '') === targetSectionId) { await dropTaskBefore(targetSectionId, moved.id, target.id); return }
  if (targetSectionId) {
    await moveTaskSection(moved, targetSectionId)
    const fresh = taskById(moved.id)
    if (fresh) await dropTaskBefore(targetSectionId, fresh.id, target.id)
  } else {
    await mutate(moved, 'section', 'PUT', { expectedVersion: moved.version, sectionId: null as unknown as string })
    const fresh = taskById(moved.id)
    if (fresh) await dropTaskBefore('', fresh.id, target.id)
  }
}
function onGroupsPointerCancel(event: PointerEvent) {
  if (!drag || drag.pointerId !== event.pointerId) return
  clearGhost(); clearDragMarks(); drag = null
}
function clearGhost() { if (drag?.row) drag.row.classList.remove('task-drag-source'); drag?.lifecycle.cleanup() }
function clearDragMarks() { groupsEl.value?.querySelectorAll('.task-drop-inside,.task-drop-before,.task-drop-after').forEach(el => el.classList.remove('task-drop-inside', 'task-drop-before', 'task-drop-after')) }

// ---------- Durable local mutations ----------
// Mutations always enter the durable local queue; the runtime owns network sync.
class LocalMutation extends TypeError {}
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  void path; void init
  throw new LocalMutation('Save locally before synchronizing')
}
function newId() { return crypto.randomUUID() }
async function queueTask(type: 'tasks.task' | 'tasks.section', id: string, version: number | null, payload: object, deleted = false, viewPayload?: object) {
  const store = await getOfflineStore(), pending = (await store.listPendingOperations()).filter(x => x.type === type && x.id === id)
  const tail = pending[pending.length - 1]
  const expectedVersion = tail ? Math.max(version ?? 0, tail.expectedVersion === null ? 1 : tail.expectedVersion + 1) : version
  const payloadWithVersion = { ...payload, ...(expectedVersion === null ? {} : { expectedVersion }) }
  const operationId = newId(), now = new Date().toISOString()
  const entity: OfflineEntity = { type, id, version: expectedVersion === null ? 1 : expectedVersion + 1, payload: viewPayload ?? payloadWithVersion, deleted, updatedAt: now }
  const operation: SyncOperation = { operationId, type, id, expectedVersion, kind: deleted ? 'delete' : 'upsert', payload: payloadWithVersion, createdAt: now }
  await saveOfflineMutation(entity, operation)
  if (viewPayload !== undefined || deleted) await (await getOfflineStore()).putEntity({ type: `${type}.view`, id, version: entity.version, payload: viewPayload ?? null, deleted, updatedAt: now })
}
async function cached<T>(type: 'tasks.task' | 'tasks.section'): Promise<T[]> {
  const store = await getOfflineStore(), rows = await store.listEntities(`${type}.view`)
  return rows.filter(row => !row.deleted).map(row => row.payload as T)
}
async function queuedIds(type: string) {
  const pending = (await (await getOfflineStore()).listPendingOperations()).filter(op => op.type === type), ids = new Set(pending.map(op => op.id))
  for (const op of pending) for (const item of ((op.payload as { order?: Array<{ id: string }> } | undefined)?.order || [])) ids.add(item.id)
  return ids
}
async function cacheRows(type: 'tasks.task' | 'tasks.section' | 'planning.project', rows: Array<{ id: string; version: number; [key: string]: unknown }>, force = false) {
  const store = await getOfflineStore(), now = new Date().toISOString(), pending = await queuedIds(type)
  const viewType = type === 'planning.project' ? 'planning.project.view' : `${type}.view`
  await Promise.all(rows.filter(row => force || !pending.has(row.id)).map(row => store.putEntity({ type: viewType, id: row.id, version: row.version, payload: JSON.parse(JSON.stringify(row)), deleted: false, updatedAt: now })))
}
let refreshGeneration = 0
async function refresh() {
  const generation = ++refreshGeneration, requestedId = String(route.params.taskId || ''), bucket = location.value
  state.busy = true
  try {
    const [allTasks, allSections, projectRows, groupOrder] = await Promise.all([
      cached<Task>('tasks.task'), cached<Section>('tasks.section'),
      getOfflineStore().then(store => store.listEntities('planning.project.view')),
      state.archive ? Promise.resolve({ version: 0, keys: [] }) : readGroupOrder(bucket),
    ])
    if (generation !== refreshGeneration) return
    state.allTasks = allTasks
    state.tasks = allTasks.filter(x => String(x.location).toLowerCase() === bucket.toLowerCase()).sort((a, b) => a.position - b.position)
    state.sections = allSections.sort((a, b) => a.position - b.position)
    state.groupOrder = groupOrder || { version: 0, keys: [] }
    state.projects = projectRows.filter(x => !x.deleted).map(x => x.payload as ProjectLabel)
    state.detail = requestedId ? allTasks.find(x => x.id === requestedId) || null : null
    if (state.detail && !state.detailEditing) { state.title = state.detail.title; state.description = state.detail.description }
    const visibleSections = state.sections.filter(section => section.isBacklogVisible !== false)
    if (visibleSections.length && !visibleSections.some(x => x.id === state.sectionId)) state.sectionId = visibleSections[0].id
  } catch (error) { state.error = (error as Error).message }
  finally { if (generation === refreshGeneration) state.busy = false }
}
function onOfflineDataUpdated() { void refresh() }

async function mutate(task: Task, suffix: string, method = 'POST', body: object = { expectedVersion: task.version }) {
  try { await request(`/${task.id}/${suffix}`, { method, body: JSON.stringify(body) }); await refresh(); return true } catch (error) {
    if (error instanceof LocalMutation) {
      const operation = suffix === 'archive' ? 'archive' : suffix === 'restore' ? 'restore' : suffix === 'section' ? 'update' : suffix === 'status' ? 'setWorkStatus' : 'move'
      const placement = suffix === 'status' || suffix === 'section' ? task.location : suffix === 'today' ? 'today' : suffix === 'backlog' || suffix === 'restore' ? 'backlog' : suffix === 'planning' ? 'planned' : 'archived'
      const payload = suffix === 'section'
        ? { operation: 'update', kind: 'task', id: task.id, expectedVersion: task.version, sectionId: (body as { sectionId: string }).sectionId }
      : { operation, kind: 'task', id: task.id, expectedVersion: task.version, placement, workStatus: suffix === 'status' ? (body as { status: string }).status : statusName(task.workStatus) === 'InProgress' ? 'inProgress' : statusName(task.workStatus).toLowerCase(), sectionId: suffix === 'planning' ? null : task.sectionId, ...(suffix === 'planning' && task.projectId && task.milestoneId && task.featureId ? { planning: { projectId: task.projectId, milestoneId: task.milestoneId, featureId: task.featureId } } : {}) }
      const local = { ...task, sectionId: suffix === 'section' ? (body as { sectionId: string }).sectionId : task.sectionId, workStatus: suffix === 'status' ? (body as { status: string }).status : suffix === 'restore' || suffix === 'backlog' || suffix === 'today' ? 'new' : task.workStatus, location: suffix === 'section' ? task.location : placement, version: task.version + 1 }
      if (suffix === 'archive') local.archivedSectionName = state.sections.find(section => section.id === task.sectionId)?.name || task.archivedSectionName
      state.allTasks = state.allTasks.map(item => item.id === task.id ? local : item)
      await queueTask('tasks.task', task.id, task.version, payload, false, local)
      state.tasks = state.tasks.filter(x => x.id !== task.id); if (String(local.location).toLowerCase() === location.value.toLowerCase()) state.tasks.push(local)
      if ((suffix === 'backlog' || suffix === 'restore') && task.sectionId) {
        const visibleSection = state.sections.find(section => section.id === task.sectionId)
        if (visibleSection) {
          const shown = { ...visibleSection, isBacklogVisible: true, version: visibleSection.version + 1 }
          state.sections = state.sections.map(section => section.id === task.sectionId ? shown : section)
          const store = await getOfflineStore(), cachedSection = await store.getEntity('tasks.section.view', task.sectionId)
          if (cachedSection) await store.putEntity({ ...cachedSection, payload: shown, deleted: false, updatedAt: new Date().toISOString() })
        }
      }
      if (state.detail?.id === task.id) state.detail = local
      await cacheRows('tasks.task', state.tasks, true)
      state.error = ''; return true
    }
    state.error = (error as Error).message; await refresh(); return false
  }
}
async function saveDetail() {
  if (!state.detail) return
  try { await request(`/${state.detail.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: state.detail.version, title: state.title, description: state.description }) }); await refresh() }
  catch (error) {
    if (!(error instanceof LocalMutation)) { state.error = (error as Error).message; return }
    const task = state.detail, payload = { operation: 'update', kind: 'task', id: task.id, expectedVersion: task.version, title: state.title, description: state.description }
    state.detail = { ...task, title: state.title, description: state.description, version: task.version + 1 }
    await queueTask('tasks.task', task.id, task.version, payload, false, state.detail); state.tasks = state.tasks.map(x => x.id === task.id ? state.detail! : x); await cacheRows('tasks.task', state.tasks, true)
    state.error = ''
  }
}
async function moveTaskSection(task: Task, sectionId: string) { await mutate(task, 'section', 'PUT', { expectedVersion: task.version, sectionId }) }
async function reorderProjectTasks(projectId: string, sourceId: string, targetId: string) {
  if (sourceId === targetId) return
  const items = state.tasks.filter(x => x.projectId === projectId).sort((a, b) => a.position - b.position), from = items.findIndex(x => x.id === sourceId), to = items.findIndex(x => x.id === targetId)
  if (from < 0 || to < 0) return
  const [moved] = items.splice(from, 1); items.splice(to, 0, moved)
  const order = items.map(x => ({ id: x.id, expectedVersion: x.version }))
  try { await request('/order', { method: 'PUT', body: JSON.stringify({ location: location.value, projectId, items: order }) }); await refresh() }
  catch (error) {
    if (error instanceof LocalMutation) {
      const source = items.find(x => x.id === sourceId)!
      await queueTask('tasks.task', source.id, source.version, { operation: 'reorder', kind: 'task', id: source.id, bucket: location.value.toLowerCase(), planning: { projectId }, order }, false, { ...source, position: items.findIndex(x => x.id === source.id), version: source.version + 1 })
      state.tasks = state.tasks.map(x => { const index = items.findIndex(i => i.id === x.id); return index < 0 ? x : { ...x, position: index, version: x.version + 1 } }); await cacheRows('tasks.task', state.tasks, true)
      state.error = ''; return
    }
    state.error = (error as Error).message
  }
}
async function dropTaskBefore(sectionId: string, sourceId: string, targetId: string) {
  if (sourceId === targetId) return
  const items = state.tasks.filter(x => (x.sectionId || '') === sectionId).sort((a, b) => a.position - b.position), from = items.findIndex(x => x.id === sourceId), to = items.findIndex(x => x.id === targetId)
  if (from < 0 || to < 0) return
  const [moved] = items.splice(from, 1); items.splice(to, 0, moved)
  try { await request('/order', { method: 'PUT', body: JSON.stringify({ location: location.value, sectionId, items: items.map(x => ({ id: x.id, expectedVersion: x.version })) }) }); await refresh() }
  catch (error) {
    if (error instanceof LocalMutation) {
      const changed = items.find(x => x.id === sourceId)!
      await queueTask('tasks.task', changed.id, changed.version, { operation: 'reorder', kind: 'task', id: changed.id, bucket: location.value.toLowerCase(), sectionId, order: items.map(x => ({ id: x.id, expectedVersion: x.version })) }, false, { ...changed, position: items.findIndex(x => x.id === changed.id), version: changed.version + 1 })
      state.tasks = state.tasks.map(x => { const index = items.findIndex(i => i.id === x.id); return index < 0 ? x : { ...x, position: index, version: x.version + 1 } }); await cacheRows('tasks.task', state.tasks, true)
      state.error = ''; return
    }
    state.error = (error as Error).message
  }
}
async function dropGroup(sourceKey: string, targetKey: string, after = false) {
  if (sourceKey === targetKey) return
  const visibleSectionIds = state.bucket === 'Backlog'
    ? state.sections.filter(section => section.isBacklogVisible !== false).map(section => section.id)
    : [...new Set(state.tasks.filter(task => task.sectionId).map(task => task.sectionId!))]
  const currentKeys = new Set([
    ...visibleSectionIds.map(id => `section:${id}`),
    ...new Set(state.tasks.filter(task => task.projectId).map(task => `project:${task.projectId}`)),
  ])
  const keys = [...state.groupOrder.keys.filter(key => currentKeys.has(key)), ...[...currentKeys].filter(key => !state.groupOrder.keys.includes(key))]
  const from = keys.indexOf(sourceKey), to = keys.indexOf(targetKey)
  if (from < 0 || to < 0) return
  keys.splice(from, 1)
  keys.splice(keys.indexOf(targetKey) + (after ? 1 : 0), 0, sourceKey)
  if (keys.every((key, index) => key === state.groupOrder.keys[index])) return
  const saveQueued = async () => {
    const order = await queueGroupOrder(location.value, state.groupOrder, keys)
    state.groupOrder = order
    localStorage.setItem(`tasks.groupOrder.${location.value}`, JSON.stringify(order))
  }
  if (!navigator.onLine || await hasPendingGroupOrder(location.value)) {
    await saveQueued()
    if (!navigator.onLine) state.error = ''
    return
  }
  try {
    const order = await request<GroupOrderView>('/groups/order', { method: 'PUT', body: JSON.stringify({ location: location.value, expectedVersion: state.groupOrder.version, keys }) })
    state.groupOrder = order
    await readGroupOrder(location.value, order)
    localStorage.setItem(`tasks.groupOrder.${location.value}`, JSON.stringify(order))
  } catch (error) {
    if (error instanceof LocalMutation) { await saveQueued(); state.error = '' }
    else { state.error = (error as Error).message; await refresh() }
  }
}

watch(() => route.fullPath, () => void refresh())
onMounted(() => {
  window.addEventListener('offline-data-updated', onOfflineDataUpdated)
  document.addEventListener('pointerdown', onDocPointerDown, true)
  document.addEventListener('keydown', onDocKeyDown)
  void refresh()
})
onBeforeUnmount(() => {
  window.removeEventListener('offline-data-updated', onOfflineDataUpdated)
  document.removeEventListener('pointerdown', onDocPointerDown, true)
  document.removeEventListener('keydown', onDocKeyDown)
  if (swipe?.timer) window.clearTimeout(swipe.timer)
  clearGhost(); drag = null
})
</script>

<template>
  <section class="tasks-section" ref="rootEl" aria-labelledby="tasks-heading" @click.capture="suppressCapture">
    <div v-if="state.error" class="task-error" role="alert">{{ state.error }} <button :aria-label="'Закрыть'" @click="state.error = ''">×</button></div>

    <template v-if="state.detail">
      <div class="detail-navigation">
        <button type="button" class="doc-action doc-back task-detail-back" @click="closeDetail">← Назад</button>
      </div>
      <section class="scroll task-detail">
        <div class="task-detail-meta">
          <button v-if="detailPlanningPath" type="button" class="task-detail-section task-detail-path" @click="router.push(`/planning/projects/${state.detail.projectId}/milestones/${state.detail.milestoneId}/features/${state.detail.featureId}`)">{{ detailPlanningPath }}</button>
          <span v-else class="task-detail-section">{{ detailOrigin }}</span>
          <button type="button" class="task-link-edit" :aria-label="detailPlanningPath ? 'Изменить фичу' : 'Назначить фичу'" @click="openLinkSheet">{{ detailPlanningPath ? '✎' : 'Назначить фичу' }}</button>
        </div>
        <div class="task-detail-title-wrap">
          <button v-if="state.detailEditing !== 'title'" type="button" class="task-title-open" @click="startTitleEdit">{{ state.detail.title }}</button>
          <input v-else ref="titleInputEl" v-model="state.title" class="task-title-detail-input" type="text" maxlength="160" @keydown.enter.prevent="finishTitleEdit(true)" @keydown.esc.prevent="finishTitleEdit(false)" @blur="finishTitleEdit(true)" @click.stop />
        </div>
        <div class="task-detail-description-card">
          <div v-if="state.detailEditing !== 'description'" class="task-description" @click="startDescEdit">{{ state.detail.description || 'Описание пока не добавлено.' }}</div>
          <textarea v-else ref="descriptionInputEl" v-model="state.description" class="task-description-input" @keydown.esc.prevent="finishDescEdit(false)" @blur="finishDescEdit(true)" @click.stop></textarea>
        </div>
        <div class="task-detail-bottom-actions">
          <button type="button" class="task-detail-pill action-move" @click="detailMove">{{ detailMoveLabel }}</button>
          <button v-if="isToday(state.detail)" type="button" class="task-detail-pill action-status" :class="workState(state.detail.workStatus)" @click="advanceTask(state.detail)">{{ workLabel(state.detail.workStatus) }}</button>
          <button v-if="!isArchived(state.detail)" type="button" class="task-detail-pill action-move action-archive" @click="archiveTask(state.detail)">В архив</button>
          <button v-else type="button" class="task-delete-icon" aria-label="Удалить задачу навсегда" @click="deleteArchivedTask(state.detail)">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16"/><path d="M9 7V4h6v3"/><path d="m6 7 1 13h10l1-13"/><path d="M10 11v5M14 11v5"/></svg>
          </button>
        </div>
      </section>
    </template>

    <div v-else class="task-board">
      <div class="task-toolbar">
        <div class="task-tabs" role="tablist" aria-label="Режим задач">
          <button type="button" class="task-tab" :class="{ active: !state.archive && state.bucket === 'Backlog' }" @click="selectBucket('Backlog')">Backlog</button>
          <button type="button" class="task-tab" :class="{ active: !state.archive && state.bucket === 'Сегодня' }" @click="selectBucket('Сегодня')">Сегодня</button>
          <button type="button" class="task-tab" :class="{ active: state.archive }" @click="openArchive">Архив</button>
        </div>
        <div class="task-toolbar-actions">
          <button type="button" class="task-icon-button" :aria-label="allOpen ? 'Свернуть все разделы' : 'Развернуть все разделы'" :title="allOpen ? 'Свернуть все разделы' : 'Развернуть все разделы'" @click="toggleAllSections">
            <svg viewBox="0 0 20 20" aria-hidden="true"><path :d="allOpen ? 'M5 3 10 8 15 3' : 'M5 8 10 3 15 8'"/><path :d="allOpen ? 'M5 17 10 12 15 17' : 'M5 12 10 17 15 12'"/></svg>
          </button>
          <button v-if="!state.archive" type="button" class="order-mode-toggle" :aria-pressed="state.orderMode" :aria-label="state.orderMode ? 'Выключить сортировку' : 'Включить сортировку'" :title="state.orderMode ? 'Выключить сортировку' : 'Включить сортировку'" @click="toggleOrderMode">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h11M4 12h11M4 17h11M19 6v12m-2.5-2.5L19 18l2.5-2.5"/></svg>
          </button>
          <button v-if="!state.archive && state.bucket === 'Backlog'" type="button" class="plus" aria-label="Создать задачу или раздел" @click="openCreateSheet">＋</button>
        </div>
      </div>

      <div v-if="!state.archive && location === 'Today'" class="task-filters">
        <button v-for="filter in filters" :key="filter.value" type="button" class="task-filter" :class="{ active: state.filter === filter.value }" @click="state.filter = filter.value"><span v-if="filter.dot" class="task-filter-dot" :class="filter.dot" />{{ filter.label }}</button>
      </div>

      <div v-if="state.archive" class="task-filters" aria-label="Фильтр архива">
        <button v-for="filter in archiveFilters" :key="filter.value" type="button" class="task-filter" :class="{ active: state.filter === filter.value }" :aria-pressed="state.filter === filter.value" @click="state.filter = filter.value"><span v-if="filter.dot" class="task-filter-dot" :class="filter.dot" />{{ filter.label }}</button>
      </div>

      <div ref="scrollEl" class="scroll task-scroll">
        <div v-if="state.archive" ref="groupsEl" class="task-groups">
          <section v-for="group in archiveGroups" :key="group.key" class="task-group">
            <div class="task-section-wrap" :data-reveal-key="group.key" :class="{ revealed: revealedKey === group.key, 'context-active': revealedKey === group.key }">
              <div class="task-section-row" :data-section-row-key="group.key" @contextmenu.prevent="rowContextMenu($event, group.key)" @pointerdown="rowPointerDown($event, group.key)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerCancel">
                <button type="button" class="chev" :class="{ open: isExpanded(group.key) }" @click="toggleSectionFor(group.key)">›</button>
                <button type="button" class="task-section-title" @click="toggleSectionFor(group.key)"><span v-if="group.kind === 'project'" class="task-project-diamond" aria-hidden="true" /><span class="task-section-label">{{ group.title }}</span></button>
                <button v-if="group.kind === 'project' || (group.kind === 'plain' && group.section.id)" type="button" class="row-menu-trigger" :aria-label="`Действия с разделом «${group.title}»`" aria-haspopup="menu" @click.stop="triggerMenu(group.key)">⋯</button>
              </div>
            </div>
            <div v-if="isExpanded(group.key)" class="task-list">
              <div v-for="task in group.tasks" :key="task.id" class="task-row-wrap" :data-reveal-key="`task:${task.id}`" :class="{ revealed: revealedKey === `task:${task.id}`, 'context-active': revealedKey === `task:${task.id}` }">
                <article class="task-row" :data-task-id="task.id" @contextmenu.prevent="rowContextMenu($event, `task:${task.id}`)" @pointerdown="rowPointerDown($event, `task:${task.id}`)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerCancel">
                  <template v-if="renamingKey === `task:${task.id}`"><input ref="renameInputEl" v-model="renamingValue" class="task-inline-input" maxlength="160" @keydown.enter.prevent="commitRename(true)" @keydown.esc.prevent="commitRename(false)" @blur="commitRename(true)" @click.stop /></template>
                  <template v-else>
                    <button type="button" class="task-open" @click="onTaskOpenClick(task)"><span class="task-status-dot" :class="workState(task.workStatus)" /><span class="task-copy"><span class="task-title">{{ task.title }}</span><span class="task-archive-status">{{ workLabel(task.workStatus) }}</span></span></button>
                    <button type="button" class="row-menu-trigger" :aria-label="`Действия с задачей «${task.title}»`" aria-haspopup="menu" @click.stop="triggerMenu(`task:${task.id}`)">⋯</button>
                  </template>
                </article>
              </div>
            </div>
          </section>
        </div>
        <div v-else ref="groupsEl" class="task-groups" @pointerdown="onGroupsPointerDown" @pointermove="onGroupsPointerMove" @pointerup="void finishDrag($event)" @pointercancel="onGroupsPointerCancel">
          <section v-for="group in groups" :key="group.key" class="task-group">
            <div class="task-section-wrap" :data-reveal-key="group.key" :class="{ revealed: revealedKey === group.key, 'context-active': revealedKey === group.key }">
              <div class="task-section-row" :data-section-row-key="group.key" :data-drag-key="group.key" :data-section-project="group.kind === 'project' ? group.projectId : ''" @contextmenu.prevent="rowContextMenu($event, group.key)" @pointerdown="rowPointerDown($event, group.key)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerCancel">
                <template v-if="renamingKey === group.key">
                  <input ref="renameInputEl" v-model="renamingValue" class="task-inline-input" maxlength="160" @keydown.enter.prevent="commitRename(true)" @keydown.esc.prevent="commitRename(false)" @blur="commitRename(true)" @click.stop />
                </template>
                <template v-else>
                  <button type="button" class="chev" :class="{ open: isExpanded(group.key) }" @click="toggleSectionFor(group.key)">›</button>
                  <button type="button" class="task-section-title" @click="toggleSectionFor(group.key)">
                    <span v-if="group.kind === 'project'" class="task-project-diamond" aria-hidden="true" />
                    <span class="task-section-label">{{ group.title }}</span>
                  </button>
                  <button type="button" class="row-menu-trigger" :hidden="state.orderMode" :aria-label="`Действия с разделом «${group.title}»`" aria-haspopup="menu" @click.stop="triggerMenu(group.key)">⋯</button>
                  <ReorderHandle v-if="state.orderMode" :drag-kind="'section'" :drag-id="group.key" :label="`Перетащить раздел ${group.title}`" />
                </template>
              </div>
            </div>
            <div v-if="isExpanded(group.key)" class="task-list">
              <div v-for="task in group.tasks" :key="task.id" class="task-row-wrap" :data-reveal-key="`task:${task.id}`" :class="{ revealed: revealedKey === `task:${task.id}`, 'context-active': revealedKey === `task:${task.id}` }">
                <article class="task-row" :data-task-id="task.id" @contextmenu.prevent="rowContextMenu($event, `task:${task.id}`)" @pointerdown="rowPointerDown($event, `task:${task.id}`)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerCancel">
                  <template v-if="renamingKey === `task:${task.id}`">
                    <input ref="renameInputEl" v-model="renamingValue" class="task-inline-input" maxlength="160" @keydown.enter.prevent="commitRename(true)" @keydown.esc.prevent="commitRename(false)" @blur="commitRename(true)" @click.stop />
                  </template>
                  <template v-else>
                    <button type="button" class="task-open" @click="onTaskOpenClick(task)">
                      <span v-if="isToday(task)" class="task-status-dot" :class="workState(task.workStatus)" />
                      <span class="task-copy"><span class="task-title">{{ task.title }}</span></span>
                    </button>
                    <button type="button" class="row-menu-trigger" :hidden="state.orderMode" :aria-label="`Действия с задачей «${task.title}»`" aria-haspopup="menu" @click.stop="triggerMenu(`task:${task.id}`)">⋯</button>
                    <ReorderHandle v-if="state.orderMode" :drag-kind="'task'" :drag-id="task.id" :label="`Перетащить ${task.title}`" />
                  </template>
                </article>
              </div>
            </div>
          </section>
        </div>
        <div v-if="showEmpty" class="task-empty">{{ emptyText }}</div>
      </div>

    </div>

    <div class="overlay" :class="{ open: state.linkSheetOpen }" @click="state.linkSheetOpen = false" />
    <section class="sheet task-link-sheet" :class="{ open: state.linkSheetOpen }" role="dialog" :aria-modal="state.linkSheetOpen ? 'true' : undefined" aria-label="Связь с планированием">
      <div class="sheet-head"><div class="sheet-title">Связь с планированием</div><button type="button" class="sheet-close" aria-label="Закрыть" @click="state.linkSheetOpen = false">×</button></div>
      <label class="task-link-label">Проект<select :value="state.linkProjectId" @change="chooseLinkProject(($event.target as HTMLSelectElement).value)"><option value="">Выбрать проект</option><option v-for="project in state.projects" :key="project.id" :value="project.id">{{ project.title }}</option></select></label>
      <label class="task-link-label">Эпик<select v-model="state.linkMilestoneId" :disabled="!selectedProject" @change="chooseLinkMilestone(state.linkMilestoneId)"><option value="">Выбрать эпик</option><option v-for="milestone in selectedProject?.milestones || []" :key="milestone.id" :value="milestone.id">{{ milestone.title }}</option></select></label>
      <label class="task-link-label">Фича<select v-model="state.linkFeatureId" :disabled="!selectedMilestone"><option value="">Выбрать фичу</option><option v-for="feature in selectedMilestone?.features || []" :key="feature.id" :value="feature.id">{{ feature.title }}</option></select></label>
      <div class="task-link-footer" :class="{ single: String(state.detail?.location).toLowerCase() !== 'backlog' }">
        <button v-if="String(state.detail?.location).toLowerCase() === 'backlog'" type="button" class="task-link-keep" :disabled="!selectedFeature" @click="savePlanningLink(false)">Оставить в Backlog</button>
        <button type="button" class="task-link-submit" :disabled="!selectedFeature || (String(state.detail?.location).toLowerCase() === 'backlog' && !canMoveLinkToPlan)" @click="savePlanningLink(String(state.detail?.location).toLowerCase() === 'backlog')">{{ String(state.detail?.location).toLowerCase() === 'backlog' ? 'В планирование' : 'Сохранить связь' }}</button>
      </div>
    </section>

    <div class="overlay" :class="{ open: state.creating }" @click="closeCreateSheet" />
    <section class="sheet task-create-sheet" :class="{ open: state.creating }" aria-label="Создание задачи">
      <div class="sheet-head">
        <div class="sheet-title">{{ state.createType === 'task' ? 'Новая задача' : 'Новый раздел' }}</div>
        <button type="button" class="sheet-close" aria-label="Закрыть" @click="closeCreateSheet">×</button>
      </div>
      <div class="type-switch">
        <button type="button" class="type-btn" :class="{ active: state.createType === 'task' }" @click="state.createType = 'task'; state.createListOpen = false">Задача</button>
        <button type="button" class="type-btn" :class="{ active: state.createType === 'section' }" @click="state.createType = 'section'; state.createListOpen = false">Раздел</button>
      </div>
      <input ref="nameFieldEl" v-model="state.title" class="name-field" :placeholder="state.createType === 'task' ? 'Название задачи' : 'Название раздела'" maxlength="160" />
      <textarea v-if="state.createType === 'task'" v-model="state.description" class="task-create-description" rows="3" placeholder="Описание задачи"></textarea>
      <button v-if="state.createType === 'task'" type="button" class="where" @click="state.createListOpen = !state.createListOpen">
        <span class="where-value">{{ state.sections.find(s => s.id === state.sectionId)?.name || 'Личное' }}</span><span class="where-arrow">›</span>
      </button>
      <div v-if="state.createType === 'task'" class="parent-list" :class="{ open: state.createListOpen }">
        <button v-for="section in state.sections.filter(item => item.isBacklogVisible !== false)" :key="section.id" type="button" class="parent-option" :class="{ selected: state.sectionId === section.id }" @click="state.sectionId = section.id; state.createListOpen = false">{{ section.name }}</button>
      </div>
      <button type="button" class="create-submit" @click="submitCreate">{{ state.createType === 'task' ? 'Создать' : 'Создать раздел' }}</button>
    </section>

    <div v-if="menu.open" ref="menuEl" class="row-context-menu" role="menu" style="position: fixed" :style="{ left: `${menu.x}px`, top: `${menu.y}px` }" :aria-label="`Действия с ${menu.kind === 'task' ? 'задачей' : 'разделом'}`">
      <button v-for="(item, index) in menu.items" :key="index" type="button" class="row-context-item" :class="{ danger: item.danger }" role="menuitem" @click="item.action(); closeMenu()">{{ item.label }}</button>
    </div>

    <div v-if="confirmBox.open" class="app-confirm-layer" @click.self="closeConfirm">
      <div class="app-confirm-card" role="dialog" aria-modal="true" aria-labelledby="task-confirm-title">
        <div class="app-confirm-title" id="task-confirm-title">{{ confirmBox.title }}</div>
        <div v-if="confirmBox.body" class="app-confirm-body">{{ confirmBox.body }}</div>
        <div class="app-confirm-actions">
          <button ref="confirmCancelEl" type="button" class="app-confirm-cancel" @click="closeConfirm">Отмена</button>
          <button type="button" class="app-confirm-accept" @click="runConfirm">{{ confirmBox.confirmLabel }}</button>
        </div>
      </div>
    </div>

  </section>
</template>
