<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{
  text: string
  start?: number | null
  length?: number | null
}>()

const parts = computed(() => {
  const start = Math.max(0, Math.min(props.text.length, props.start ?? 0))
  const end = Math.max(start, Math.min(props.text.length, start + (props.length ?? 0)))
  return {
    before: props.text.slice(0, start),
    match: props.text.slice(start, end),
    after: props.text.slice(end),
  }
})
</script>

<template>
  <template v-if="parts.match">
    {{ parts.before }}<mark class="semantic-match">{{ parts.match }}</mark>{{ parts.after }}
  </template>
  <template v-else>{{ text }}</template>
</template>
