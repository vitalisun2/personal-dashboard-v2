import { getOfflineStore, withSyncPaused } from './runtime'
import type { OfflineEntity, SyncOperation } from './types'

type Data = Record<string, any>
export type EntityConflict = {
  type: string; id: string; title: string; local: unknown; server: unknown
  localDeleted: boolean; serverDeleted: boolean; operationIds: string[]; serverVersion: number | null
}

function normalize(payload: unknown): Data {
  if (!payload || typeof payload !== 'object') return {}
  const result: Data = {}
  for (const [key, value] of Object.entries(payload)) result[key[0]!.toLowerCase() + key.slice(1)] = value
  if (result.description === undefined && result.body !== undefined) result.description = result.body
  if (result.location === undefined && result.placement !== undefined) result.location = result.placement
  if (result.isArchived === undefined && result.archived !== undefined) result.isArchived = result.archived
  if (result.status === undefined && result.featureStatus !== undefined) result.status = result.featureStatus
  return result
}

function localView(entities: OfflineEntity[], type: string, id: string): Data {
  if (type === 'planning.milestone' || type === 'planning.feature') {
    for (const entity of entities.filter(item => item.type === 'planning.project.view' && !item.deleted)) {
      const project = normalize(entity.payload)
      for (const milestone of project.milestones ?? []) {
        if (type === 'planning.milestone' && milestone.id === id) return { ...milestone, projectId: entity.id }
        const feature = milestone.features?.find((item: Data) => item.id === id)
        if (feature) return { ...feature, projectId: entity.id, milestoneId: milestone.id }
      }
    }
  }
  return normalize((entities.find(item => item.type === `${type}.view` && item.id === id)
    ?? entities.find(item => item.type === type && item.id === id))?.payload)
}

export async function listEntityConflicts(): Promise<EntityConflict[]> {
  const store = await getOfflineStore()
  const [conflicts, pending, entities] = await Promise.all([store.listConflicts(), store.listPendingOperations(), store.listEntities()])
  const grouped = new Map<string, EntityConflict>()
  for (const conflict of conflicts) {
    const operations = pending.filter(item => item.type === conflict.type && item.id === conflict.id)
    if (!operations.length) continue
    const local = localView(entities, conflict.type, conflict.id)
    const server = normalize(conflict.serverPayload)
    const key = `${conflict.type}:${conflict.id}`, previous = grouped.get(key)
    if (previous && (previous.serverVersion ?? -1) > (conflict.serverVersion ?? -1)) continue
    grouped.set(key, { type: conflict.type, id: conflict.id, title: local.title ?? local.name ?? server.title ?? 'Без названия',
      local, server, localDeleted: operations.at(-1)!.kind === 'delete',
      serverDeleted: conflict.serverDeleted === true || conflict.serverVersion === null,
      serverVersion: conflict.serverVersion, operationIds: operations.map(item => item.operationId) })
  }
  return [...grouped.values()]
}

function replacementPayload(conflict: EntityConflict, local: Data, source: Data): Data {
  const base = { id: conflict.id, expectedVersion: conflict.serverVersion }
  if (conflict.type === 'knowledge.node') return { ...local, id: conflict.id, version: conflict.serverVersion }
  if (conflict.type === 'tasks.task') return { ...base, operation: 'replace', kind: 'task', title: local.title,
    description: local.description ?? '', placement: String(local.location).toLowerCase(), workStatus: typeof local.workStatus === 'number' ? ['new', 'inProgress', 'done'][local.workStatus] : local.workStatus,
    sectionId: local.sectionId ?? null, position: local.position, archivedSectionName: local.archivedSectionName,
    planning: local.projectId ? { projectId: local.projectId, milestoneId: local.milestoneId, featureId: local.featureId } : null }
  if (conflict.type.startsWith('planning.')) return { ...base, operation: 'replace', kind: conflict.type.split('.')[1],
    projectId: local.projectId ?? source.projectId, milestoneId: local.milestoneId ?? source.milestoneId,
    title: local.title, description: local.description ?? '', featureStatus: typeof local.status === 'number' ? ['planned', 'active', 'done'][local.status] : local.status, isArchived: local.isArchived, position: local.position }
  if (conflict.type === 'tasks.section') return { ...base, operation: 'update', kind: 'section', title: local.name ?? local.title, bucket: local.location ?? local.bucket }
  if (conflict.type === 'tasks.groupOrder') return { keys: local.keys }
  throw new Error('Этот тип конфликта пока не поддерживает выбор локальной версии.')
}

function updateViews(entities: OfflineEntity[], conflict: EntityConflict, value: Data, deleted: boolean, version: number): OfflineEntity[] {
  const type = conflict.type, id = conflict.id
  const existing = localView(entities, type, id)
  const payload: Data = { ...existing, ...value, id, version }
  if (type === 'tasks.section') { payload.name = value.title ?? value.name; payload.location = value.bucket ?? value.location }
  const record: OfflineEntity = { type, id, version, payload: deleted ? null : payload, deleted }
  const changes = [record]
  if (type.startsWith('tasks.') && type !== 'tasks.groupOrder' || type.startsWith('planning.')) changes.push({ ...record, type: `${type}.view` })
  if (type === 'planning.milestone' || type === 'planning.feature') {
    for (const entity of entities.filter(item => item.type === 'planning.project.view' && !item.deleted)) {
      const project = structuredClone(normalize(entity.payload))
      let changed = false
      project.milestones = (project.milestones ?? []).flatMap((milestone: Data) => {
        if (type === 'planning.milestone' && milestone.id === id) { changed = true; return deleted ? [] : [{ ...milestone, ...payload }] }
        milestone.features = (milestone.features ?? []).flatMap((feature: Data) => {
          if (type === 'planning.feature' && feature.id === id) { changed = true; return deleted ? [] : [{ ...feature, ...payload }] }
          return [feature]
        })
        return [milestone]
      })
      if (changed) changes.push({ ...entity, payload: project })
    }
  }
  return changes
}

export async function resolveEntityConflict(conflict: EntityConflict, choice: 'local' | 'server'): Promise<void> {
  await withSyncPaused(async () => {
    const store = await getOfflineStore()
    const fresh = (await listEntityConflicts()).find(item => item.type === conflict.type && item.id === conflict.id)
    if (!fresh || JSON.stringify(fresh) !== JSON.stringify(conflict)) throw new Error('Версии изменились. Откройте конфликт заново и проверьте обе версии.')
    const [pending, entities] = await Promise.all([store.listPendingOperations(), store.listEntities()])
    const operations = pending.filter(item => item.type === conflict.type && item.id === conflict.id)
    const replacements: SyncOperation[] = []
    let value = normalize(choice === 'local' ? conflict.local : conflict.server)
    let deleted = choice === 'local' ? conflict.localDeleted : conflict.serverDeleted
    let version = conflict.serverVersion ?? 0
    if (choice === 'local' && !(conflict.localDeleted && conflict.serverDeleted)) {
      if (!conflict.localDeleted && operations.some(item => normalize(item.payload).operation === 'reorder'))
        throw new Error('Конфликт порядка затрагивает несколько записей. Локальные изменения сохранены. Примите серверный порядок и повторите перестановку.')
      if (conflict.serverDeleted && !conflict.localDeleted)
        throw new Error('Запись удалена на сервере. Локальная версия сохранена. Скопируйте её в новую запись перед принятием удаления.')
      const source = normalize(operations[0]?.payload)
      const payload = deleted ? { ...source, operation: 'delete', kind: conflict.type.split('.')[1], id: conflict.id, expectedVersion: version } : replacementPayload(conflict, value, source)
      const latestTime = pending.reduce((time, item) => Math.max(time, Date.parse(item.createdAt)), Date.now())
      replacements.push({ operationId: crypto.randomUUID(), type: conflict.type, id: conflict.id, expectedVersion: version,
        kind: deleted ? 'delete' : 'upsert', payload, createdAt: new Date(latestTime + 1).toISOString(), retryCount: 1 })
      version++
    }
    await store.resolveEntityOperations(conflict.type, conflict.id, pending.map(item => item.operationId), conflict.serverVersion, replacements,
      updateViews(entities, conflict, value, deleted, version))
    window.dispatchEvent(new Event('offline-data-updated'))
  })
}
