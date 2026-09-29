import type { IndexedDbOfflineStore } from './db'
import type { OfflineEntity } from './types'
import { fetchWithTimeout } from './network.ts'

type Row = { id: string; version: number; milestones?: Row[]; features?: Row[] }
const groupIds = ['3be630ad-c973-4cb2-b8d3-6374d87c8355', '83b28844-904d-42fd-9640-c16606137f86']

/** Warm every editable section, including lists the user has never opened. */
export async function refreshOfflineSnapshots(store: IndexedDbOfflineStore): Promise<boolean> {
  const urls = ['/api/v2/planning/projects?includeArchived=true', '/api/v2/tasks',
    '/api/v2/tasks/sections/all',
    '/api/v2/tasks/groups/order?location=Backlog', '/api/v2/tasks/groups/order?location=Today']
  const data = await Promise.all(urls.map(async url => {
    const response = await fetchWithTimeout(url, { cache: 'no-store' })
    if (!response.ok) throw new Error(`Offline snapshot failed: ${response.status}`)
    return response.json()
  }))
  const entities: OfflineEntity[] = []
  const add = (type: string, rows: Row[]) => {
    for (const row of rows) entities.push({ type: `${type}.view`, id: row.id, version: row.version, payload: row, deleted: false })
  }
  add('planning.project', data[0])
  for (const project of data[0] as Row[]) {
    add('planning.milestone', project.milestones ?? [])
    for (const milestone of project.milestones ?? []) add('planning.feature', milestone.features ?? [])
  }
  add('tasks.task', data[1])
  add('tasks.section', data[2])
  groupIds.forEach((id, index) => entities.push({ type: 'tasks.groupOrder', id, version: data[index + 3].version, payload: { keys: data[index + 3].keys }, deleted: false }))
  return store.replaceViewSnapshots(entities)
}
