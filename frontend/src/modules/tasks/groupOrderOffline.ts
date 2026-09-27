import { getOfflineStore, saveOfflineMutation } from '../../offline/runtime'

export type GroupOrderView = { version: number; keys: string[] }

const type = 'tasks.groupOrder'
const ids: Record<string, string> = {
  Backlog: '3be630ad-c973-4cb2-b8d3-6374d87c8355',
  Today: '83b28844-904d-42fd-9640-c16606137f86',
}

function groupId(location: string): string {
  const id = ids[location]
  if (!id) throw new Error(`Неизвестный список задач: ${location}`)
  return id
}

export async function hasPendingGroupOrder(location: string): Promise<boolean> {
  const id = groupId(location)
  return (await (await getOfflineStore()).listPendingOperations()).some(operation => operation.type === type && operation.id === id)
}

export async function readGroupOrder(location: string, remote?: GroupOrderView): Promise<GroupOrderView | null> {
  const id = groupId(location), store = await getOfflineStore()
  const cached = await store.getEntity<{ keys: string[] }>(type, id)
  if (await hasPendingGroupOrder(location))
    return cached?.payload ? { version: cached.version, keys: cached.payload.keys } : null
  if (remote) {
    if (cached?.payload && cached.version > remote.version) return { version: cached.version, keys: cached.payload.keys }
    await store.putEntity({ type, id, version: remote.version, payload: { keys: remote.keys }, deleted: false, updatedAt: new Date().toISOString() })
    return remote
  }
  return cached?.payload ? { version: cached.version, keys: cached.payload.keys } : null
}

export async function queueGroupOrder(location: string, current: GroupOrderView, keys: string[]): Promise<GroupOrderView> {
  const id = groupId(location), store = await getOfflineStore()
  const pending = (await store.listPendingOperations()).filter(operation => operation.type === type && operation.id === id)
  const tail = pending[pending.length - 1]
  const expectedVersion = tail ? (tail.expectedVersion ?? 0) + 1 : current.version
  const order = { version: expectedVersion + 1, keys }
  const now = new Date().toISOString()
  await saveOfflineMutation(
    { type, id, version: order.version, payload: { keys }, deleted: false, updatedAt: now },
    { operationId: crypto.randomUUID(), type, id, expectedVersion, kind: 'upsert', payload: { keys }, createdAt: now },
  )
  return order
}
