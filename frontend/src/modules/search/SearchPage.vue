<script setup lang="ts">
import { ref, watch } from 'vue'

const props = defineProps<{
  initialQuery?: string
}>()

const query = ref(props.initialQuery ?? '')
watch(() => props.initialQuery, value => { if (value !== undefined) query.value = value })
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

    <p v-if="query.trim()" class="search-status" role="status">Функционал не реализован</p>
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
@media (max-width: 560px) { .search-page { padding: 16px; } }
</style>
