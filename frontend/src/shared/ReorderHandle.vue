<script setup lang="ts">
withDefaults(defineProps<{ label: string; class?: string; dragKind?: string; dragId?: string }>(), { class: 'handle' })
</script>

<template>
  <button type="button" :class="['handle', $props.class]" :data-drag-kind="dragKind" :data-drag-id="dragId" :aria-label="label">
    <span aria-hidden="true"><i v-for="dot in 6" :key="dot" /></span>
  </button>
</template>
