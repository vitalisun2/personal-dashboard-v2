<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { applyTheme, readTheme, saveTheme, themeStorageKey, type Theme } from '../shared/theme'

const dialog = ref<HTMLDialogElement | null>(null)
const theme = ref<Theme>(readTheme())
const options = [{ value: 'light', label: 'Светлая' }, { value: 'dark', label: 'Тёмная' }] as const

function selectTheme(next: Theme) {
  theme.value = next
  saveTheme(next)
}

function syncTheme(event: StorageEvent) {
  if (event.key !== themeStorageKey && event.key !== null) return
  theme.value = readTheme()
  applyTheme(theme.value)
}

onMounted(() => window.addEventListener('storage', syncTheme))
onUnmounted(() => window.removeEventListener('storage', syncTheme))
</script>

<template>
  <button class="chat-head-btn settings-trigger" type="button" aria-label="Настройки" title="Настройки" @click="dialog?.showModal()">
    <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m9.5 3-.6 2.1-1.6.9-2.1-.5-2.5 4.3 1.5 1.6v1.8l-1.5 1.6 2.5 4.3 2.1-.5 1.6.9.6 2.1h5l.6-2.1 1.6-.9 2.1.5 2.5-4.3-1.5-1.6v-1.8l1.5-1.6-2.5-4.3-2.1.5-1.6-.9-.6-2.1Z"/><circle cx="12" cy="12" r="3"/></svg>
  </button>
  <dialog ref="dialog" class="appearance-dialog" aria-labelledby="settings-title" @click="event => { if (event.target === dialog) dialog?.close() }">
    <div class="appearance-panel">
      <header class="appearance-header">
        <h2 id="settings-title">Настройки</h2>
        <button type="button" class="appearance-close" aria-label="Закрыть настройки" autofocus @click="dialog?.close()">×</button>
      </header>
      <fieldset class="appearance-options">
        <legend>Оформление</legend>
        <label v-for="option in options" :key="option.value" class="appearance-option" :class="{ selected: theme === option.value }">
          <svg class="appearance-symbol" viewBox="0 0 24 24" aria-hidden="true">
            <template v-if="option.value === 'light'"><circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5"/></template>
            <path v-else d="M20.5 13A8.5 8.5 0 0 1 11 3.5 8.5 8.5 0 1 0 20.5 13Z"/>
          </svg>
          <span>{{ option.label }}</span>
          <input type="radio" name="appearance" :value="option.value" :checked="theme === option.value" @change="selectTheme(option.value)" />
        </label>
      </fieldset>
      <p class="appearance-note">Тема сохраняется в этом браузере.</p>
    </div>
  </dialog>
</template>

<style scoped>
.appearance-dialog { width: min(360px, calc(100% - 32px)); max-height: calc(100dvh - 32px); padding: 0; border: 1px solid var(--line); border-radius: 20px; background: var(--bg); color: var(--text); box-shadow: 0 24px 70px #0003; }
.appearance-dialog::backdrop { background: var(--overlay); }
.appearance-panel { padding: 20px; }
.appearance-header { display: flex; align-items: center; justify-content: space-between; gap: 12px; margin-bottom: 20px; }
.appearance-header h2 { margin: 0; font-size: 20px; }
.appearance-close { width: 40px; height: 40px; border-radius: 10px; background: var(--surface); color: var(--text); font-size: 26px; }
.appearance-options { display: grid; gap: 10px; margin: 0; padding: 0; border: 0; }
.appearance-options legend { margin-bottom: 12px; padding: 0; font-size: 14px; font-weight: 650; }
.appearance-option { display: flex; align-items: center; gap: 12px; min-height: 58px; padding: 12px 14px; border: 1px solid var(--line); border-radius: 12px; background: var(--surface); cursor: pointer; font-size: 14px; }
.appearance-option.selected { border-color: var(--text); background: var(--surface-2); }
.appearance-symbol { width: 24px; height: 24px; flex: none; fill: none; stroke: currentColor; stroke-width: 1.6; stroke-linecap: round; stroke-linejoin: round; }
.appearance-option input { margin: 0 0 0 auto; width: 18px; height: 18px; accent-color: var(--text); }
.appearance-note { margin: 16px 0 0; color: var(--muted); font-size: 12px; line-height: 1.5; }
.appearance-close:hover { background: var(--surface-2); }
.appearance-close:focus-visible, .appearance-option:focus-within { outline: 2px solid var(--accent); outline-offset: 3px; }
</style>
