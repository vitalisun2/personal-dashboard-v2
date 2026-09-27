import type { RouteLocationRaw } from 'vue-router'

export type ChatArea = 'knowledge' | 'tasks' | 'planning' | 'general'

export function chatRoute(area: ChatArea = 'general', focus?: {
  entityType: string
  entityId: string
  entityVersion: number
}): RouteLocationRaw {
  return {
    path: '/chat',
    query: focus
      ? {
          area,
          mode: 'entity',
          entityType: focus.entityType,
          entityId: focus.entityId,
          entityVersion: String(focus.entityVersion),
        }
      : { area, mode: 'general' },
  }
}
