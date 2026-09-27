<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { searchKnowledge, type KnowledgeSearchResult } from '../knowledge/knowledgeApi'

const props = defineProps<{
  initialQuery?: string
}>()

const query = ref(props.initialQuery ?? '')
const results = ref<KnowledgeSearchResult[]>([])
const loading = ref(false)
const error = ref('')
let searchTimer: ReturnType<typeof setTimeout> | undefined
let searchRevision = 0

async function runSearch() {
  const revision = ++searchRevision
  const term = query.value.trim()
  results.value = []
  error.value = ''
  if (!term) {
    loading.value = false
    return
  }

  loading.value = true
  try {
    const found = await searchKnowledge(term)
    if (revision === searchRevision) results.value = found
  } catch (cause) {
    if (revision === searchRevision) error.value = cause instanceof Error ? cause.message : 'Не удалось выполнить поиск.'
  } finally {
    if (revision === searchRevision) loading.value = false
  }
}

function scheduleSearch() {
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = setTimeout(() => void runSearch(), 250)
}

watch(() => props.initialQuery, value => { if (value !== undefined) query.value = value })
watch(query, scheduleSearch, { immediate: true })
onBeforeUnmount(() => {
  if (searchTimer) clearTimeout(searchTimer)
  searchRevision++
})
</script>

<template>
  <main class="search-page">
    <header class="search-header">
      <h1>Поиск</h1>
      <label class="search-input">
        <span aria-hidden="true">⌕</span>
        <input v-model="query" type="search" autocomplete="off" placeholder="Поиск по знаниям, плану, задачам и чату…" autofocus />
        <button v-if="query" type="button" aria-label="Очистить поиск" @click="query = ''">×</button>
      </label>
    </header>

    <p v-if="loading" class="search-status" role="status">Ищем…</p>
    <p v-else-if="error" class="search-error" role="alert">{{ error }}</p>
    <template v-else-if="query.trim()">
      <p v-if="results.length" class="search-status" role="status">Найдено в {{ results.length }} документах</p>
      <ol v-if="results.length" class="search-results">
        <li v-for="result in results" :key="result.id">
          <RouterLink class="result-title" :to="`/knowledge/${result.id}`">{{ result.title }}</RouterLink>
          <p class="result-path">{{ result.path }}</p>
          <blockquote class="result-snippet">«{{ result.snippet }}»</blockquote>
        </li>
      </ol>
      <p v-else class="search-status">Ничего не найдено</p>
    </template>
  </main>
</template>

<style scoped>
.search-page { width: min(100%, 900px); margin: 0 auto; color: #e5edf5; }
.search-header { display: grid; gap: 16px; }
.search-input { display: flex; align-items: center; gap: 10px; min-height: 48px; padding: 0 14px; border: 1px solid #293744; border-radius: 12px; background: #18242f; }
.search-input input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; color: inherit; font: inherit; }
.search-input input::placeholder { color: #91a0af; }
.search-input button { border: 0; background: none; color: #aab7c4; font-size: 22px; cursor: pointer; }
.search-status { color: #aab7c4; }
.search-error { color: #f09390; }
.search-results { display: grid; gap: 14px; margin: 22px 0; padding: 0; list-style: none; }
.search-results li { padding: 16px; border: 1px solid #293744; border-radius: 12px; background: #18242f; }
.result-title { color: #e5edf5; font-weight: 650; text-decoration: none; }
.result-title:hover { color: #b9d6ff; }
.result-path { margin: 6px 0; color: #91a0af; font-size: 12px; }
.result-snippet { margin: 10px 0 0; padding-left: 12px; border-left: 2px solid #42617f; color: #c5d0dc; line-height: 1.5; }
@media (max-width: 560px) { .search-page { padding: 16px; } }
</style>
