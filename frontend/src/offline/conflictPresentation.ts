import type { EntityConflict } from './conflicts'
import type { OfflineEntity } from './types'

type Data = Record<string, any>
export type ConflictField = { key: string; label: string; local: string; server: string; expandable?: boolean }
type Value = { key: string; label: string; value: unknown; text: string; expandable?: boolean }

function data(payload: unknown): Data {
  if (!payload || typeof payload !== 'object') return {}
  return Object.fromEntries(Object.entries(payload).map(([key, value]) => [key[0]!.toLowerCase() + key.slice(1), value]))
}

function enumValue(value: unknown, options: string[]): string {
  return typeof value === 'number' ? options[value] ?? String(value) : String(value ?? '').toLowerCase()
}
const names: Record<string, string> = { backlog: 'Backlog', today: 'Сегодня', planned: 'В плане', archived: 'Архив',
  new: 'Новая', inprogress: 'В работе', done: 'Готово', active: 'В работе', document: 'Документ', section: 'Раздел' }

/** Compare meaningful values, not transport aliases, IDs, or generated paths. */
export function conflictFields(conflict: EntityConflict, entities: OfflineEntity[]): ConflictField[] {
  const records = new Map<string, Data>()
  const put = (type: string, id: string, payload: unknown) => records.set(`${type}:${id}`, data(payload))
  for (const entity of [...entities].sort((a, b) => Number(a.type.endsWith('.view')) - Number(b.type.endsWith('.view')))) {
    if (entity.deleted) continue
    const type = entity.type.replace(/\.view$/, ''), payload = data(entity.payload)
    put(type, entity.id, payload)
    if (type === 'planning.project') for (const milestone of payload.milestones ?? []) {
      put('planning.milestone', milestone.id, milestone)
      for (const feature of milestone.features ?? []) put('planning.feature', feature.id, feature)
    }
  }
  function title(type: string, id: unknown, fallback: string): string {
    const record = records.get(`${type}:${id}`)
    return record?.title ?? record?.name ?? fallback
  }
  function knowledgeParents(parentId: unknown, seen = new Set<unknown>()): string[] {
    if (!parentId) return []
    if (seen.has(parentId)) return ['Раздел недоступен']
    seen.add(parentId)
    const parent = records.get(`knowledge.node:${parentId}`)
    return parent ? [...knowledgeParents(parent.parentId, seen), parent.title ?? 'Без названия'] : ['Раздел недоступен']
  }
  function fields(payload: unknown, deleted: boolean, side: 'local' | 'server'): Value[] {
    const p = data(payload), result: Value[] = []
    const add = (key: string, label: string, value: unknown, text = String(value ?? '') || 'Не указано', expandable = false) =>
      result.push({ key, label, value, text, expandable })
    add('deleted', 'Запись', deleted, deleted ? 'Удалена' : 'Сохранена')
    add('title', 'Название', p.title ?? p.name ?? '')
    const knowledge = conflict.type === 'knowledge.node'
    add('description', knowledge ? 'Текст документа' : 'Описание', knowledge ? p.markdown ?? '' : p.description ?? p.body ?? '', undefined, true)
    if (conflict.type === 'tasks.task' || conflict.type === 'planning.feature') {
      const status = conflict.type === 'tasks.task' ? enumValue(p.workStatus, ['new', 'inprogress', 'done'])
        : enumValue(p.status ?? p.featureStatus, ['planned', 'active', 'done'])
      add('status', 'Статус', status, names[status] ?? (status || 'Не указан'))
    }
    const location = enumValue(p.location ?? p.placement ?? p.bucket,
      ['planned', 'backlog', 'today', 'archived'])
    const planning = data(p.planning)
    const projectId = p.projectId ?? planning.projectId ?? null
    const milestoneId = p.milestoneId ?? planning.milestoneId ?? null
    const featureId = p.featureId ?? planning.featureId ?? null
    const sectionId = p.sectionId ?? null, parentId = p.parentId ?? null
    const parts: string[] = []
    if (knowledge) {
      const serverParents = side === 'server' && parentId && typeof p.path === 'string' ? p.path.split(' / ').slice(0, -1) : []
      parts.push('База знаний', ...(serverParents.length ? serverParents : knowledgeParents(parentId)))
    }
    else {
      if (conflict.type.startsWith('tasks.')) parts.push(names[location] ?? (location || 'Список не указан'))
      else parts.push('Планирование')
      if (sectionId) parts.push(title('tasks.section', sectionId, 'Раздел недоступен'))
      if (projectId) parts.push(title('planning.project', projectId, 'Проект недоступен'))
      if (milestoneId) parts.push(title('planning.milestone', milestoneId, 'Эпик недоступен'))
      if (featureId) parts.push(title('planning.feature', featureId, 'Фича недоступна'))
      if (location === 'archived' && p.archivedSectionName) parts.push(p.archivedSectionName)
    }
    add('location', 'Расположение', knowledge ? [parentId] : [location, sectionId, projectId, milestoneId, featureId,
      location === 'archived' ? p.archivedSectionName ?? null : null], parts.join(' → '))
    const archived = Boolean(p.isArchived ?? p.archived ?? false)
    add('archived', 'В архиве', archived, archived ? 'Да' : 'Нет')
    // Entity replacements also carry position. Keep meaningful order differences visible.
    if (p.position !== undefined) add('position', 'Место в списке', p.position, `${Number(p.position) + 1}`)
    if (p.planningPosition !== undefined) add('planningPosition', 'Место в плане', p.planningPosition, `${Number(p.planningPosition) + 1}`)
    if (p.isBacklogVisible !== undefined) add('backlogVisible', 'Показывать в Backlog', p.isBacklogVisible, p.isBacklogVisible ? 'Да' : 'Нет')
    return result
  }
  const local = fields(conflict.local, conflict.localDeleted, 'local'), server = fields(conflict.server, conflict.serverDeleted, 'server')
  const keys = [...new Set([...local, ...server].map(field => field.key))]
  return keys.flatMap(key => {
    const left = local.find(field => field.key === key), right = server.find(field => field.key === key)
    if (key !== 'deleted' && conflict.localDeleted && conflict.serverDeleted) return []
    if (key !== 'deleted' && !conflict.localDeleted && !conflict.serverDeleted && JSON.stringify(left?.value) === JSON.stringify(right?.value)) return []
    if (key === 'deleted' && conflict.localDeleted === conflict.serverDeleted) return []
    const field = left ?? right!
    return [{ key, label: field.label, expandable: field.expandable,
      local: conflict.localDeleted && key !== 'deleted' ? 'Удалено' : left?.text ?? 'Не указано',
      server: conflict.serverDeleted && key !== 'deleted' ? 'Удалено' : right?.text ?? 'Не указано' }]
  })
}
