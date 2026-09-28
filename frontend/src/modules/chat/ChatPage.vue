<script setup lang="ts">
import { computed, shallowRef, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ChatView from './ChatView.vue'
import type { ChatArea } from '../../shared/chatRoute'

const route = useRoute()
const router = useRouter()
const areas: ChatArea[] = ['knowledge', 'tasks', 'planning', 'general']
const chatQuery = shallowRef({ ...route.query })
watch(() => route.fullPath, () => {
  if (route.path === '/chat') chatQuery.value = { ...route.query }
})
const area = computed<ChatArea>(() => areas.includes(chatQuery.value.area as ChatArea) ? chatQuery.value.area as ChatArea : 'general')
const entity = computed(() => {
  const query = chatQuery.value
  if (query.mode !== 'entity' || typeof query.entityType !== 'string' || typeof query.entityId !== 'string') return undefined
  const entityArea = query.entityType === 'knowledge.document' ? 'knowledge'
    : query.entityType === 'tasks.task' ? 'tasks'
      : query.entityType.startsWith('planning.') ? 'planning' : undefined
  if (entityArea !== area.value) return undefined
  const version = Number(query.entityVersion)
  if (!Number.isInteger(version) || version < 1) return undefined
  const label = query.entityType === 'knowledge.document' ? 'Этот документ'
    : query.entityType === 'tasks.task' ? 'Эта задача'
      : `Этот объект: ${query.entityType}`
  return { entityType: query.entityType, entityId: query.entityId, entityVersion: version, label }
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
