<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ChatView from './ChatView.vue'
import type { ChatArea } from '../../shared/chatRoute'

const route = useRoute()
const router = useRouter()
const areas: ChatArea[] = ['knowledge', 'tasks', 'planning', 'general']
const area = computed<ChatArea>(() => areas.includes(route.query.area as ChatArea) ? route.query.area as ChatArea : 'general')
const entity = computed(() => {
  if (route.query.mode !== 'entity' || typeof route.query.entityType !== 'string' || typeof route.query.entityId !== 'string') return undefined
  const entityArea = route.query.entityType === 'knowledge.document' ? 'knowledge'
    : route.query.entityType === 'tasks.task' ? 'tasks'
      : route.query.entityType.startsWith('planning.') ? 'planning' : undefined
  if (entityArea !== area.value) return undefined
  const version = Number(route.query.entityVersion)
  if (!Number.isInteger(version) || version < 1) return undefined
  const label = route.query.entityType === 'knowledge.document' ? 'Этот документ'
    : route.query.entityType === 'tasks.task' ? 'Эта задача'
      : `Этот объект: ${route.query.entityType}`
  return { entityType: route.query.entityType, entityId: route.query.entityId, entityVersion: version, label }
})
const chatSessionKey = computed(() => [area.value, entity.value?.entityType, entity.value?.entityId, entity.value?.entityVersion].join(':'))

function back() {
  if (window.history.length > 1) router.back()
  else void router.push('/knowledge')
}
</script>

<template>
  <ChatView :key="chatSessionKey" :area="area" :entity="entity" @back="back" />
</template>
