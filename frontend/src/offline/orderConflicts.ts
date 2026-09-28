import { fetchWithTimeout } from './network'
import type { IndexedDbOfflineStore } from './db'
import type { OfflineEntity, SyncConflict, SyncOperation } from './types'
import type { EntityConflict } from './conflicts'

type Row = Record<string, any> & { id: string; version: number }
type Payload = Record<string, any>
export type OrderPreview = {
  scope: string; local: Array<{ id: string; title: string }>; server: Array<{ id: string; title: string }>
  addedTitles: string[]; removedTitles: string[]; error?: string
  rows: Row[]; excluded: Array<{ id: string; row?: Row }>; parentVersion?: number; ancestorVersion?: number; source: Payload
}
const snapshotType = 'sync.orderSnapshot'
const title = (row: Payload) => String(row.title ?? row.name ?? 'Без названия')
const lower = (value: unknown) => String(value ?? '').toLowerCase()
const data = (operation: SyncOperation): Payload => operation.payload as Payload ?? {}

export function orderScope(operation: SyncOperation): string | undefined {
  const p = data(operation), type = operation.type
  if (type === 'tasks.groupOrder') return JSON.stringify([type, operation.id])
  if (p.operation !== 'reorder') return undefined
  if (type === 'knowledge.node') return JSON.stringify([type, p.parentId ?? null])
  if (type.startsWith('planning.')) return JSON.stringify([type, type === 'planning.project' ? null : p.projectId ?? null,
    type === 'planning.feature' ? p.milestoneId ?? null : null])
  if (type === 'tasks.section') return JSON.stringify([type, lower(p.bucket)])
  if (type === 'tasks.task') return JSON.stringify([type, lower(p.placement ?? p.bucket), p.planning?.projectId ?? null,
    p.planning?.milestoneId ?? null, p.planning?.featureId ?? null, p.sectionId ?? null])
  return undefined
}

async function download(operation: SyncOperation): Promise<{ rows: Row[]; all: Row[]; parentVersion?: number; ancestorVersion?: number }> {
  const p = data(operation), type = operation.type
  const get = async (url: string) => {
    const response = await fetchWithTimeout(url, { cache: 'no-store' })
    if (!response.ok) throw new Error('Не удалось загрузить серверный порядок. Попробуйте открыть конфликт при доступном сервере.')
    return response.json()
  }
  if (type.startsWith('planning.')) {
    const projects: Row[] = await get('/api/v2/planning/projects?includeArchived=true')
    if (type === 'planning.project') return { rows: projects, all: projects }
    const project = projects.find(row => row.id === p.projectId)
    if (!project) return { rows: [], all: projects.flatMap(row => type === 'planning.milestone' ? row.milestones : row.milestones.flatMap((m: Row) => m.features)) }
    if (type === 'planning.milestone') return { rows: project.milestones, all: projects.flatMap(row => row.milestones), parentVersion: project.version }
    const milestone = project.milestones.find((row: Row) => row.id === p.milestoneId)
    if (!milestone) return { rows: [], all: projects.flatMap(row => row.milestones.flatMap((m: Row) => m.features)) }
    return { rows: milestone.features, all: projects.flatMap(row => row.milestones.flatMap((m: Row) => m.features)), parentVersion: milestone.version, ancestorVersion: project.version }
  }
  if (type === 'knowledge.node') {
    const all: Row[] = await get('/api/v2/knowledge/tree')
    return { rows: all.filter(row => (row.parentId ?? null) === (p.parentId ?? null)), all }
  }
  const bucket = type === 'tasks.groupOrder'
    ? operation.id === '83b28844-904d-42fd-9640-c16606137f86' ? 'Today' : 'Backlog'
    : lower(p.placement ?? p.bucket) === 'today' ? 'Today' : 'Backlog'
  if (type === 'tasks.section') {
    const rows: Row[] = await get(`/api/v2/tasks/sections?location=${bucket}`)
    return { rows, all: rows }
  }
  if (type === 'tasks.groupOrder') {
    const [order, sections, projects] = await Promise.all([get(`/api/v2/tasks/groups/order?location=${bucket}`),
      get(`/api/v2/tasks/sections?location=${bucket}`), get('/api/v2/planning/projects?includeArchived=true')])
    const rows = order.keys.map((key: string, position: number) => {
      const [kind, id] = key.split(':')
      const row = (kind === 'section' ? sections : projects).find((item: Row) => item.id === id)
      return { id: key, version: order.version, title: row ? title(row) : 'Недоступный элемент', position }
    })
    return { rows, all: rows, parentVersion: order.version }
  }
  const all: Row[] = await get('/api/v2/tasks')
  const rows = all.filter(row => lower(row.location) === lower(p.placement ?? p.bucket) &&
    (p.planning?.projectId ? row.projectId === p.planning.projectId &&
      (lower(p.placement) !== 'planned' || row.milestoneId === p.planning.milestoneId && row.featureId === p.planning.featureId)
      : !row.projectId && (row.sectionId ?? null) === (p.sectionId ?? null)))
  return { rows, all }
}

async function snapshot(store: IndexedDbOfflineStore, operation: SyncOperation, fresh: boolean) {
  const key = orderScope(operation)!
  const cached = await store.getEntity<Awaited<ReturnType<typeof download>>>(snapshotType, key)
  if (!fresh && cached?.payload && Date.now() - Date.parse(cached.updatedAt ?? '') < 10_000) return cached.payload
  try {
    const value = await download(operation)
    value.rows.sort((a, b) => (a.position ?? 0) - (b.position ?? 0))
    await store.putEntity({ type: snapshotType, id: key, version: 0, deleted: false, payload: value, updatedAt: new Date().toISOString() })
    return value
  } catch (error) {
    if (!fresh && cached?.payload) return cached.payload
    if (error instanceof TypeError || error instanceof DOMException)
      throw new Error('Сервер недоступен. Оба порядка сохранены; подтвердите выбор после восстановления связи.')
    throw error
  }
}

function cachedTitle(entities: OfflineEntity[], type: string, id: string): string {
  if (type === 'tasks.groupOrder') {
    const [kind, entityId] = id.split(':')
    return cachedTitle(entities, kind === 'section' ? 'tasks.section' : 'planning.project', entityId!)
  }
  const item = entities.find(row => row.type === `${type}.view` && row.id === id) ?? entities.find(row => row.type === type && row.id === id)
  return title(item?.payload ?? {})
}

export async function listOrderConflicts(store: IndexedDbOfflineStore, conflicts: SyncConflict[], pending: SyncOperation[], entities: OfflineEntity[], fresh = false): Promise<EntityConflict[]> {
  const scopes = new Set(conflicts.map(conflict => pending.find(op => op.operationId === conflict.operationId))
    .filter((op): op is SyncOperation => !!op).map(orderScope).filter((scope): scope is string => !!scope))
  return Promise.all([...scopes].map(async scope => {
    const operations = pending.filter(op => orderScope(op) === scope), last = operations.at(-1)!
    const relevant = conflicts.filter(conflict => operations.some(op => op.operationId === conflict.operationId))
    const sourceConflict = [...relevant].sort((a, b) => (b.serverVersion ?? -1) - (a.serverVersion ?? -1))[0]!
    const labels: Record<string, string> = { 'planning.project': 'Порядок проектов', 'planning.milestone': 'Порядок эпиков',
      'planning.feature': 'Порядок фич', 'tasks.task': 'Порядок задач', 'tasks.section': 'Порядок разделов', 'tasks.groupOrder': 'Порядок групп задач', 'knowledge.node': 'Порядок в базе знаний' }
    const order: OrderPreview = { scope, local: [], server: [], addedTitles: [], removedTitles: [], rows: [], excluded: [], source: data(last) }
    try {
      const remote = await snapshot(store, last, fresh)
      const ids: string[] = last.type === 'tasks.groupOrder' ? data(last).keys : (data(last).order ?? []).map((row: Payload) => row.id)
      const known = new Set(remote.rows.map(row => row.id)), uniqueIds = [...new Set(ids)]
      const selectedIds = [...uniqueIds.filter(id => known.has(id)), ...remote.rows.filter(row => !ids.includes(row.id)).map(row => row.id)]
      const byId = new Map(remote.rows.map(row => [row.id, row]))
      order.local = selectedIds.map(id => ({ id, title: title(byId.get(id)!) }))
      order.server = remote.rows.map(row => ({ id: row.id, title: title(row) }))
      order.addedTitles = remote.rows.filter(row => !ids.includes(row.id)).map(title)
      order.excluded = uniqueIds.filter(id => !known.has(id)).map(id => ({ id, row: remote.all.find(row => row.id === id) }))
      order.removedTitles = order.excluded.map(row => cachedTitle(entities, last.type, row.id))
      order.rows = remote.rows; order.parentVersion = remote.parentVersion; order.ancestorVersion = remote.ancestorVersion
      const memberIds = new Set([...ids, ...known])
      const membershipPending = pending.some(op => {
        if (op.type !== last.type || !memberIds.has(op.id) || orderScope(op)) return false
        const payload = data(op)
        return op.kind === 'delete' || ['create', 'move', 'archive', 'restore'].includes(payload.operation)
          || 'placement' in payload || 'planning' in payload || 'sectionId' in payload
          || last.type === 'knowledge.node' && 'parentId' in payload && payload.parentId !== byId.get(op.id)?.parentId
      })
      if (membershipPending || order.excluded.some(row => pending.some(op => op.id === row.id && !orderScope(op))))
        order.error = 'В этом списке есть ещё не синхронизированные изменения состава. Сначала синхронизируйте или разберите их конфликты, затем откройте порядок заново.'
    } catch (error) { order.error = (error as Error).message }
    const p = data(last)
    const parentId = last.type === 'planning.feature' ? p.milestoneId : last.type === 'planning.milestone' ? p.projectId
      : last.type === 'knowledge.node' ? p.parentId : p.planning?.featureId ?? p.planning?.projectId ?? p.sectionId
    const parentType = last.type === 'planning.feature' ? 'planning.milestone' : last.type === 'knowledge.node' ? 'knowledge.node'
      : p.planning?.featureId ? 'planning.feature' : p.sectionId ? 'tasks.section' : 'planning.project'
    const context = parentId ? cachedTitle(entities, parentType, parentId) : last.type.startsWith('tasks.')
      ? (last.type === 'tasks.groupOrder' ? last.id === '83b28844-904d-42fd-9640-c16606137f86' : lower(p.bucket) === 'today') ? 'Сегодня' : 'Backlog' : ''
    return { type: last.type, id: sourceConflict.id, title: `${labels[last.type] ?? 'Порядок элементов'}${context ? ` · ${context}` : ''}`,
      local: {}, server: {}, localDeleted: false, serverDeleted: false,
      operationIds: operations.map(op => op.operationId), serverVersion: sourceConflict.serverVersion, order }
  }))
}

export function orderReplacement(conflict: EntityConflict, pending: SyncOperation[]): SyncOperation[] {
  const order = conflict.order!, rows = new Map(order.rows.map(row => [row.id, row]))
  if (!order.local.length && conflict.type !== 'tasks.groupOrder') return []
  const id = conflict.type === 'tasks.groupOrder' ? conflict.id : order.local[0]!.id
  const version = conflict.type === 'tasks.groupOrder' ? order.parentVersion! : rows.get(id)!.version
  const payload = conflict.type === 'tasks.groupOrder' ? { keys: order.local.map(row => row.id) }
    : { ...order.source, id, expectedVersion: version, ...(order.parentVersion === undefined ? {} : { expectedParentVersion: order.parentVersion }),
      order: order.local.map(row => ({ id: row.id, expectedVersion: rows.get(row.id)!.version,
        ...(conflict.type === 'knowledge.node' ? { previousPosition: rows.get(row.id)!.position } : {}) })) }
  const time = pending.reduce((max, op) => Math.max(max, Date.parse(op.createdAt)), Date.now())
  return [{ operationId: crypto.randomUUID(), type: conflict.type, id, expectedVersion: version, kind: 'upsert', payload, createdAt: new Date(time + 1).toISOString() }]
}

/** Change positions only; existing local content and unrelated queued edits survive. */
export function orderViewChanges(conflict: EntityConflict, choice: 'local' | 'server', entities: OfflineEntity[], pending: SyncOperation[]): OfflineEntity[] {
  const order = conflict.order!, selected = order[choice], type = conflict.type
  const pendingContent = (id: string) => pending.some(op => op.type === type && op.id === id && !orderScope(op))
  if (type === 'tasks.groupOrder') return [{ type, id: conflict.id, version: order.parentVersion! + (choice === 'local' ? 1 : 0), deleted: false, payload: { keys: selected.map(row => row.id) } }]
  const changes: OfflineEntity[] = []
  const rowById = new Map(order.rows.map(row => [row.id, row]))
  const positioned = new Map(selected.map(({ id }, position) => {
    const server = rowById.get(id)!
    const cached = entities.find(row => row.type === `${type}.view` && row.id === id) ?? entities.find(row => row.type === type && row.id === id)
    const increment = choice === 'local' && (type.startsWith('tasks.') || server.position !== position) ? 1 : 0
    const version = pendingContent(id) ? cached?.version ?? server.version : server.version + increment
    const payload = { ...server, ...(cached?.payload as Payload ?? {}), position, version }
    const record = { type: type === 'knowledge.node' ? type : `${type}.view`, id, version, payload, deleted: false }
    changes.push(record)
    return [id, payload]
  }))
  for (const { id, row } of order.excluded) if (!pendingContent(id)) {
    const previous = entities.find(item => item.type === `${type}.view` && item.id === id) ?? entities.find(item => item.type === type && item.id === id)
    if (previous) changes.push({ ...previous, version: row?.version ?? previous.version, payload: row ?? null, deleted: !row })
  }
  if (type === 'planning.milestone' || type === 'planning.feature') {
    const entity = entities.find(row => row.type === 'planning.project.view' && row.id === order.source.projectId)
    if (entity?.payload) {
      const project = structuredClone(entity.payload) as Payload
      const increment = choice === 'local' && selected.some(({ id }, position) => rowById.get(id)?.position !== position) ? 1 : 0
      const otherPending = pending.some(op => !conflict.operationIds.includes(op.operationId) &&
        (op.id === order.source.projectId || data(op).projectId === order.source.projectId))
      const merge = (rows: Row[]) => selected.map(({ id }) => {
        const existing = rows.find(row => row.id === id)
        return { ...rowById.get(id), ...existing, ...positioned.get(id), ...(existing?.features ? { features: existing.features } : {}) }
      })
      if (type === 'planning.milestone') {
        project.milestones = merge(project.milestones)
        if (!otherPending && order.parentVersion !== undefined) project.version = order.parentVersion + increment
      }
      else {
        const milestone = project.milestones.find((row: Row) => row.id === order.source.milestoneId)
        if (milestone) {
          milestone.features = merge(milestone.features)
          if (!otherPending && order.parentVersion !== undefined) milestone.version = order.parentVersion + increment
        }
        if (!otherPending && order.ancestorVersion !== undefined) project.version = order.ancestorVersion + increment
      }
      changes.push({ ...entity, version: project.version, payload: project })
    }
  }
  return changes
}
