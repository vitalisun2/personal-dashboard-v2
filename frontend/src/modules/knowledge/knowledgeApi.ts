import { fetchWithTimeout } from '../../offline/network'
import { getOfflineStore, saveOfflineMutation } from '../../offline/runtime'
import type { OfflineEntity, SyncConflict, SyncOperation } from '../../offline/types'

export type KnowledgeNode = {
  id: string
  kind: 'section' | 'document'
  title: string
  markdown: string
  parentId: string | null
  position: number
  version: number
  path: string
  archived: boolean
}

export type KnowledgeSearchResult = {
  id: string
  title: string
  parentId: string | null
  path: string
  snippet: string
  version: number
  updatedAt: string
}

export const ENTITY_TYPE = 'knowledge.node'
type KnowledgeReorder = KnowledgeNode & { operation: 'reorder'; order: Array<{ id: string; expectedVersion: number; previousPosition?: number }> }

function isKnowledgeReorder(payload: unknown): payload is KnowledgeReorder {
  return !!payload && typeof payload === 'object' && (payload as KnowledgeReorder).operation === 'reorder' && Array.isArray((payload as KnowledgeReorder).order)
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetchWithTimeout(url, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { detail?: string } | null
    throw new Error(body?.detail || `Ошибка запроса (${response.status})`)
  }
  return response.json() as Promise<T>
}

export async function getCachedKnowledge(): Promise<KnowledgeNode[]> {
  const store = await getOfflineStore()
  const entities = await store.listEntities<KnowledgeNode>(ENTITY_TYPE)
  const active = entities.filter(entity => !entity.deleted && entity.payload && !entity.payload.archived)
  const byId = new Map(active.map(entity => [entity.id, entity.payload!]))
  // One reorder is a sibling-scope intent. Its optimistic projection must also survive reload.
  for (const operation of await store.listPendingOperations()) {
    if (operation.type !== ENTITY_TYPE || !isKnowledgeReorder(operation.payload)) continue
    for (const [position, entry] of operation.payload.order.entries()) {
      const node = byId.get(entry.id)
      if (!node || node.parentId !== operation.payload.parentId) continue
      const changed = (entry.previousPosition ?? node.position) !== position
      byId.set(node.id, { ...node, position, version: Math.max(node.version, entry.expectedVersion + (changed ? 1 : 0)) })
    }
  }
  const valid = new Set<string>()
  for (const node of byId.values()) {
    if (!node.parentId) {
      valid.add(node.id)
      continue
    }
    const parent = byId.get(node.parentId)
    if (parent?.kind === 'section') valid.add(node.id)
  }
  let changed = true
  while (changed) {
    changed = false
    for (const id of valid) {
      const node = byId.get(id)!
      if (node.parentId && !valid.has(node.parentId)) { valid.delete(id); changed = true }
    }
  }
  return [...byId.values()].filter(node => valid.has(node.id))
}

export async function cacheServerKnowledge(nodes: KnowledgeNode[]): Promise<void> {
  const store = await getOfflineStore()
  const pending = await store.listPendingOperations()
  const pendingIds = new Set(pending.filter(operation => operation.type === ENTITY_TYPE).map(operation => operation.id))
  for (const operation of pending) if (operation.type === ENTITY_TYPE && isKnowledgeReorder(operation.payload)) {
    for (const entry of operation.payload.order) pendingIds.add(entry.id)
  }
  for (const node of nodes) {
    if (pendingIds.has(node.id)) continue
    const cached = await store.getEntity<KnowledgeNode>(ENTITY_TYPE, node.id)
    if (!cached || node.version >= cached.version) await store.putEntity({ type: ENTITY_TYPE, id: node.id, version: node.version, payload: node, deleted: false })
  }
}

export async function loadKnowledgeTree(): Promise<KnowledgeNode[]> {
  return request<KnowledgeNode[]>('/api/v2/knowledge/tree')
}

export function searchKnowledge(query: string): Promise<KnowledgeSearchResult[]> {
  return request<KnowledgeSearchResult[]>(`/api/v2/knowledge/search?q=${encodeURIComponent(query)}`)
}

export async function getLocallyDeletedKnowledgeIds(): Promise<Set<string>> {
  const entities = await (await getOfflineStore()).listEntities<KnowledgeNode>(ENTITY_TYPE)
  const hidden = new Set(entities.filter(entity => entity.deleted).map(entity => entity.id))
  let changed = true
  while (changed) {
    changed = false
    for (const entity of entities) {
      if (hidden.has(entity.id) || !entity.payload?.parentId || !hidden.has(entity.payload.parentId)) continue
      hidden.add(entity.id)
      changed = true
    }
  }
  return hidden
}

async function nextOperationTime(entityId: string): Promise<string> {
  const pending = await (await getOfflineStore()).listPendingOperations()
  const last = pending.filter(operation => operation.type === ENTITY_TYPE && operation.id === entityId)
    .reduce((time, operation) => Math.max(time, Date.parse(operation.createdAt)), 0)
  return new Date(Math.max(Date.now(), last + 1)).toISOString()
}

export async function queueKnowledgeUpsert(node: KnowledgeNode, baseVersion: number | null): Promise<void> {
  const createdAt = await nextOperationTime(node.id)
  const expectedVersion = baseVersion
  const operation: SyncOperation<KnowledgeNode> = {
    operationId: crypto.randomUUID(), type: ENTITY_TYPE, id: node.id,
    expectedVersion, kind: 'upsert', payload: node, createdAt,
  }
  const entity: OfflineEntity<KnowledgeNode> = { type: ENTITY_TYPE, id: node.id, version: expectedVersion === null ? 1 : expectedVersion + 1, payload: node, deleted: false }
  await saveOfflineMutation(entity, operation)
}

export async function queueKnowledgeReorder(node: KnowledgeNode, ordered: KnowledgeNode[]): Promise<void> {
  if (ordered.some(item => item.parentId !== node.parentId) || !ordered.some(item => item.id === node.id)) throw new Error('Порядок должен содержать узлы одного раздела.')
  const position = ordered.findIndex(item => item.id === node.id)
  const updated = { ...node, position, version: node.version + (node.position === position ? 0 : 1) }
  const payload: KnowledgeReorder = { ...updated, operation: 'reorder', order: ordered.map(item => ({ id: item.id, expectedVersion: item.version, previousPosition: item.position })) }
  await saveOfflineMutation({ type: ENTITY_TYPE, id: node.id, version: updated.version, payload: updated, deleted: false }, {
    operationId: crypto.randomUUID(), type: ENTITY_TYPE, id: node.id, expectedVersion: node.version,
    kind: 'upsert', payload, createdAt: await nextOperationTime(node.id),
  })
}

export async function queueKnowledgeDelete(node: KnowledgeNode): Promise<void> {
  const createdAt = await nextOperationTime(node.id)
  const operation: SyncOperation = {
    operationId: crypto.randomUUID(), type: ENTITY_TYPE, id: node.id,
    expectedVersion: node.version, kind: 'delete', createdAt,
  }
  await saveOfflineMutation({ type: ENTITY_TYPE, id: node.id, version: node.version + 1, payload: null, deleted: true }, operation)
}

export async function getKnowledgeConflicts(): Promise<SyncConflict<KnowledgeNode>[]> {
  return (await getOfflineStore()).listConflicts()
    .then(conflicts => conflicts.filter(conflict => conflict.type === ENTITY_TYPE) as SyncConflict<KnowledgeNode>[])
}

export async function retryKnowledgeDeleteConflict(conflict: SyncConflict<KnowledgeNode>): Promise<boolean> {
  if (!conflict.localDeleted)
    throw new Error('Удаление нельзя повторить для текущего состояния документа.')
  const store = await getOfflineStore()
  if (conflict.serverDeleted || conflict.serverVersion === null) {
    await store.resolveConflict(conflict.operationId)
    return false
  }
  const operation: SyncOperation = {
    operationId: crypto.randomUUID(), type: ENTITY_TYPE, id: conflict.id,
    expectedVersion: conflict.serverVersion, kind: 'delete', createdAt: new Date().toISOString(), retryCount: 1,
  }
  await store.replaceOperation(conflict.operationId, operation)
  return true
}

export async function keepKnowledgeServerVersion(conflict: SyncConflict<KnowledgeNode>): Promise<void> {
  const entity = conflict.serverVersion === null ? undefined : {
    type: ENTITY_TYPE,
    id: conflict.id,
    version: conflict.serverVersion,
    payload: conflict.serverDeleted ? null : conflict.serverPayload,
    deleted: conflict.serverDeleted ?? false,
  }
  await (await getOfflineStore()).resolveConflict(conflict.operationId, entity)
}

export async function pendingKnowledgeCount(): Promise<number> {
  return (await getOfflineStore()).listPendingOperations().then(operations => operations.filter(operation => operation.type === ENTITY_TYPE).length)
}
