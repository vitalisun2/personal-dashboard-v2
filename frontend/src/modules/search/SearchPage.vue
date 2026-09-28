<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import SearchHighlight from '../../shared/SearchHighlight.vue'
import { readViewState, writeViewState } from '../../shared/uiViewState'
import { getLocallyDeletedKnowledgeIds, searchKnowledge, type KnowledgeSearchResult } from '../knowledge/knowledgeApi'
import { search as searchIndexed, type SearchHit } from './searchApi'

const props = defineProps<{
  initialQuery?: string
}>()

const savedQuery = readViewState('search.query', '')
const query = ref(props.initialQuery ?? (typeof savedQuery === 'string' ? savedQuery : ''))
const exactResults = ref<KnowledgeSearchResult[]>([])
const indexedHits = ref<SearchHit[]>([])
const exactLoading = ref(false)
const semanticLoading = ref(false)
const error = ref('')
const semanticError = ref('')
const locallyDeletedIds = ref(new Set<string>())
let searchTimer: ReturnType<typeof setTimeout> | undefined
let searchRevision = 0

const similarResults = computed(() => {
  const exactIds = new Set(exactResults.value.map(result => result.id))
  return indexedHits.value.filter(hit => hit.source.kind === 'knowledge.document' &&
    !exactIds.has(hit.source.id) && !locallyDeletedIds.value.has(hit.source.id))
})
const loading = computed(() => exactLoading.value || semanticLoading.value)
const hasResults = computed(() => exactResults.value.length > 0 || similarResults.value.length > 0)

function semanticProximity(hit: SearchHit) {
  return Math.max(0, Math.min(1, hit.semanticSimilarity ?? 0)).toFixed(2).replace('.', ',')
}

async function runSearch() {
  const revision = ++searchRevision
  const term = query.value.trim()
  exactResults.value = []
  indexedHits.value = []
  error.value = ''
  semanticError.value = ''
  if (!term) {
    exactLoading.value = false
    semanticLoading.value = false
    return
  }

  exactLoading.value = true
  semanticLoading.value = true
  const hiddenSearch = getLocallyDeletedKnowledgeIds().catch(() => new Set<string>())
    .then(ids => { if (revision === searchRevision) locallyDeletedIds.value = ids; return ids })
  const exactSearch = Promise.all([searchKnowledge(term), hiddenSearch])
    .then(([found, hidden]) => { if (revision === searchRevision) exactResults.value = found.filter(result => !hidden.has(result.id)) })
    .catch(cause => { if (revision === searchRevision) error.value = cause instanceof Error ? cause.message : 'Не удалось выполнить поиск.' })
    .finally(() => { if (revision === searchRevision) exactLoading.value = false })
  const semanticSearch = searchIndexed({ query: term, mode: 'relevant', kinds: ['knowledge.document'], pageSize: 5, matchMode: 'semantic' })
    .then(found => { if (revision === searchRevision) indexedHits.value = found.hits })
    .catch(() => { if (revision === searchRevision) semanticError.value = 'Поиск по смыслу временно недоступен.' })
    .finally(() => { if (revision === searchRevision) semanticLoading.value = false })
  await Promise.allSettled([exactSearch, semanticSearch, hiddenSearch])
}

function scheduleSearch() {
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = setTimeout(() => void runSearch(), 250)
}

watch(() => props.initialQuery, value => { if (value !== undefined) query.value = value })
watch(query, value => { writeViewState('search.query', value); scheduleSearch() }, { immediate: true })
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

    <template v-if="query.trim()">
      <p v-if="error" class="search-error" role="alert">{{ error }}</p>
      <section v-if="exactResults.length" class="result-group">
        <h2>Точные совпадения</h2>
        <ol class="search-results">
          <li v-for="result in exactResults" :key="result.id">
          <RouterLink class="result-title" :to="`/knowledge/${result.id}`"><SearchHighlight :text="result.title" :query="query" /></RouterLink>
          <p class="result-path">{{ result.path }}</p>
          <blockquote class="result-snippet">«<SearchHighlight :text="result.snippet" :query="query" />»</blockquote>
          </li>
        </ol>
      </section>
      <section v-if="similarResults.length" class="result-group">
        <h2>Похожие по смыслу</h2>
        <ol class="search-results">
          <li v-for="result in similarResults" :key="result.source.id">
            <RouterLink class="result-title" :to="`/knowledge/${result.source.id}`">{{ result.source.title }}</RouterLink>
            <p class="semantic-score" :title="`Сходство по смыслу с запросом, не вероятность правильного ответа`" :aria-label="`Близость: ${semanticProximity(result)}. Сходство по смыслу с запросом, не вероятность правильного ответа`">Близость: {{ semanticProximity(result) }}</p>
            <p class="result-path">{{ result.source.path }}</p>
            <blockquote class="result-snippet">«{{ result.source.snippet }}»</blockquote>
          </li>
        </ol>
      </section>
      <p v-if="semanticLoading && exactResults.length" class="search-status" role="status">Ищем похожие по смыслу…</p>
      <p v-else-if="loading && !hasResults" class="search-status" role="status">Ищем…</p>
      <p v-if="semanticError" class="search-error" role="status">{{ semanticError }}</p>
      <p v-if="!loading && !hasResults && !error" class="search-status">Ничего не найдено</p>
    </template>
  </main>
</template>

<style scoped>
.search-page { width: min(100%, 900px); margin: 0 auto; color: var(--text); }
.search-header { display: grid; gap: 16px; }
.search-input { display: flex; align-items: center; gap: 10px; min-height: 48px; padding: 0 14px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
.search-input input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; color: inherit; font: inherit; }
.search-input input::placeholder { color: var(--muted); }
.search-input button { border: 0; background: none; color: var(--text-secondary); font-size: 22px; cursor: pointer; }
.search-status { color: var(--text-secondary); }
.search-error { color: var(--danger); }
.result-group { margin-top: 22px; }
.result-group h2 { margin: 0 0 10px; color: var(--text-secondary); font-size: 13px; font-weight: 650; }
.search-results { display: grid; gap: 14px; margin: 0; padding: 0; list-style: none; }
.search-results li { padding: 16px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); }
.result-title { color: var(--text); font-weight: 650; text-decoration: none; }
.result-title:hover { color: var(--accent); }
.semantic-score { margin: 4px 0 0; color: var(--muted); font-size: 11px; font-weight: 400; }
.result-path { margin: 6px 0; color: var(--muted); font-size: 12px; }
.result-snippet { margin: 10px 0 0; padding-left: 12px; border-left: 2px solid var(--line-strong); color: var(--text-secondary); line-height: 1.5; }
.result-snippet :deep(mark.exact), .result-title :deep(mark.exact) { padding: 0 2px; border-radius: 3px; background: var(--highlight-blue); color: var(--accent); font-weight: 750; }
@media (max-width: 560px) { .search-page { padding: 16px; } }
</style>
