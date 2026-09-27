<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
  text: string
  query: string
}>()

const parts = computed(() => {
  const query = props.query.trim()
  if (!query) return [{ text: props.text, match: false }]
  const escaped = query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const singleWord = [...query].every(character => /[\p{L}\p{N}_]/u.test(character))
  const pattern = singleWord
    ? `(?<![\\p{L}\\p{N}_])(${escaped})(?![\\p{L}\\p{N}_])`
    : `(${escaped})`
  return props.text.split(new RegExp(pattern, 'giu'))
    .filter(Boolean)
    .map(text => ({ text, match: text.localeCompare(query, undefined, { sensitivity: 'accent' }) === 0 }))
})
</script>

<template>
  <template v-for="(part, index) in parts" :key="index">
    <mark v-if="part.match" class="exact">{{ part.text }}</mark>
    <template v-else>{{ part.text }}</template>
  </template>
</template>
