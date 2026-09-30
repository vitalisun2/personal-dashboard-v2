<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'
import { requestSync, subscribeSyncStatus, type SyncStatus } from '../offline/runtime'
import AppearanceSettings from './AppearanceSettings.vue'
import ConflictResolver from '../offline/ConflictResolver.vue'
import { readViewState, writeViewState } from '../shared/uiViewState'

const route = useRoute()
const navigation = [
  { path: '/knowledge', label: 'База знаний', icon: '▤' },
  { path: '/planning', label: 'Планирование', icon: '▦' },
  { path: '/tasks', label: 'Задачи', icon: '☑\uFE0E' },
  { path: '/testing', label: 'Тестирование', icon: '◈' },
]
const currentSection = computed(() => String(route.path.split('/')[1] || 'knowledge'))
const savedRoutes = readViewState<Record<string, string>>('routes', {})
const lastRouteBySection = reactive(new Map<string, string>(
  Object.entries(savedRoutes).filter(([section, path]) =>
    ['knowledge', 'planning', 'tasks', 'chat', 'search', 'testing'].includes(section)
    && typeof path === 'string' && (path === `/${section}` || path.startsWith(`/${section}/`) || path.startsWith(`/${section}?`))),
))
if (route.path !== '/') lastRouteBySection.set(currentSection.value, route.fullPath)
function saveNavigation(path: string) {
  writeViewState('activeRoute', path)
  writeViewState('routes', Object.fromEntries(lastRouteBySection))
}
watch(() => route.fullPath, (nextPath, previousPath) => {
  const nextSection = nextPath.split('/')[1] || 'knowledge'
  const previousSection = previousPath.split('/')[1] || 'knowledge'
  if (previousPath !== '/') lastRouteBySection.set(previousSection, previousPath)
  lastRouteBySection.set(nextSection, nextPath)
  saveNavigation(nextPath)
  if (previousPath !== '/' && nextSection !== previousSection && ['knowledge', 'planning', 'tasks'].includes(nextSection)) requestSync()
}, { flush: 'sync' })
function destinationForSection(section: string) { return lastRouteBySection.get(section) || `/${section}` }
function destinationForChat() {
  return lastRouteBySection.get('chat') || { path: '/chat', query: { area: chatArea.value } }
}
const titles: Record<string, string> = { knowledge: 'База знаний', planning: 'Планирование', tasks: 'Задачи', chat: 'Агент', search: 'Поиск', testing: 'Тестирование' }
const title = computed(() => titles[currentSection.value] || 'База знаний')
const chatArea = computed(() => {
  const section = currentSection.value
  if (section === 'chat') {
    const area = route.query.area
    return ['knowledge', 'tasks', 'planning', 'general'].includes(String(area)) ? area : 'general'
  }
  return ['knowledge', 'tasks', 'planning'].includes(section) ? section : 'general'
})
const hideBottomNav = computed(() => currentSection.value === 'chat')
const syncStatus = ref<SyncStatus>('syncing')
const waitingForSync = 'Сохранено локально, ожидает синхронизации'
const syncLabels: Record<SyncStatus, string> = {
  ready: 'Синхронизировано', syncing: 'Проверяем синхронизацию…', pending: waitingForSync,
  offline: waitingForSync, conflict: waitingForSync, error: waitingForSync,
}
let unsubscribeSyncStatus: (() => void) | undefined
const appFrame = ref<HTMLElement | null>(null)
let viewport: VisualViewport | null = null
const pullDistance = ref(0)
const pullReady = computed(() => pullDistance.value >= 64)
let pullStart: { x: number; y: number; scroll: HTMLElement | null } | null = null

function resetPull() {
  pullStart = null
  pullDistance.value = 0
}

function onTouchStart(event: TouchEvent) {
  resetPull()
  if (event.touches.length !== 1 || !window.matchMedia('(pointer: coarse)').matches) return
  const target = event.target
  if (!(target instanceof Element) || !target.closest('.app-body')) return
  if (target.closest('input, textarea, select, [contenteditable], .handle, .bottom-window, .sheet, .overlay, .row-context-menu, .chat-history-overlay')) return
  const shell = target.closest('.phone-shell')!
  if (shell.querySelector('.overlay.open, .sheet.open, [aria-modal="true"], .chat-history-overlay.open')) return
  const scroll = target.closest<HTMLElement>('.scroll, .chat-scroll')
    ?? shell.querySelector<HTMLElement>('.page-content .scroll, .page-content .chat-scroll')
  if (scroll && scroll.scrollTop > 0) return
  pullStart = { x: event.touches[0].clientX, y: event.touches[0].clientY, scroll }
}

function onTouchMove(event: TouchEvent) {
  if (!pullStart || event.touches.length !== 1) return
  if (pullStart.scroll && pullStart.scroll.scrollTop > 0) { resetPull(); return }
  const dx = event.touches[0].clientX - pullStart.x
  const dy = event.touches[0].clientY - pullStart.y
  if (dy < -8 || (Math.abs(dx) > 10 && Math.abs(dx) > Math.abs(dy))) { resetPull(); return }
  if (dy <= 0) return
  pullDistance.value = Math.min(96, dy * 0.65)
  if (pullDistance.value > 0) event.preventDefault()
}

function onTouchEnd() {
  const shouldReload = pullReady.value
  resetPull()
  if (shouldReload) window.location.reload()
}

function syncVisibleViewport() {
  const frame = appFrame.value
  if (!frame) return
  frame.style.setProperty('--visible-viewport-height', `${Math.max(0, viewport?.height ?? window.innerHeight)}px`)
  frame.style.setProperty('--visible-viewport-offset-top', `${Math.max(0, viewport?.offsetTop ?? 0)}px`)
}

onMounted(() => {
  if (route.path !== '/') saveNavigation(route.fullPath)
  unsubscribeSyncStatus = subscribeSyncStatus(next => {
    // A background check should not flash a new message; keep the last save state.
    if (next !== 'syncing') syncStatus.value = next
  })
  document.documentElement.classList.add('keyboard-viewport-lock')
  viewport = window.visualViewport ?? null
  syncVisibleViewport()
  viewport?.addEventListener('resize', syncVisibleViewport)
  viewport?.addEventListener('scroll', syncVisibleViewport)
  window.addEventListener('resize', syncVisibleViewport)
})
onUnmounted(() => {
  unsubscribeSyncStatus?.()
  viewport?.removeEventListener('resize', syncVisibleViewport)
  viewport?.removeEventListener('scroll', syncVisibleViewport)
  window.removeEventListener('resize', syncVisibleViewport)
  document.documentElement.classList.remove('keyboard-viewport-lock')
})
</script>

<template>
  <main ref="appFrame" class="app-frame" @touchstart.passive="onTouchStart" @touchmove="onTouchMove" @touchend="onTouchEnd" @touchcancel="resetPull">
    <div class="phone-shell">
      <div v-if="pullDistance > 32" class="pull-refresh-indicator" role="status">
        {{ pullReady ? 'Отпустите для обновления' : 'Потяните для обновления' }}
      </div>
      <div class="app-body" :class="{ 'is-pulling': pullDistance > 0 }" :style="pullDistance > 0 ? { transform: `translateY(${pullDistance}px)` } : undefined">
        <header class="topline">
          <div class="header-copy">
            <div class="eyebrow">Personal OS</div>
            <h1 class="heading">{{ title }}</h1>
          </div>
          <RouterLink class="chat-head-btn" :to="destinationForChat()" aria-label="Открыть чат с агентом" title="Чат с агентом">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 17.2 4 20v-4.9A7.4 7.4 0 0 1 3 11.4C3 7.3 6.8 4 11.5 4S20 7.3 20 11.4s-3.8 7.4-8.5 7.4c-1.6 0-3.1-.4-4.3-1.1Z"/><path d="m16.9 2.7.45 1.15 1.15.45-1.15.45-.45 1.15-.45-1.15-1.15-.45 1.15-.45.45-1.15Z"/></svg>
          </RouterLink>
          <AppearanceSettings />
        </header>
        <span class="sync-status" :data-status="syncStatus" aria-live="polite">{{ syncLabels[syncStatus] }}</span>
        <ConflictResolver />
        <div class="page-content">
          <RouterView v-slot="{ Component }">
            <KeepAlive>
              <component :is="Component" :key="currentSection" />
            </KeepAlive>
          </RouterView>
        </div>
      </div>
      <nav class="bottom-window" :class="{ 'chat-navigation': hideBottomNav }" aria-label="Навигация">
        <div class="sidebar-brand">Personal OS</div>
        <RouterLink v-for="item in navigation" :key="item.path" :to="destinationForSection(item.path.slice(1))" class="bottom-item" :class="{ active: currentSection === item.path.slice(1) }" :aria-current="currentSection === item.path.slice(1) ? 'page' : undefined">
          <span class="nav-icon" aria-hidden="true">{{ item.icon }}</span><span>{{ item.label }}</span>
        </RouterLink>
        <RouterLink class="bottom-item tablet-only" :class="{ active: currentSection === 'chat' }" :to="destinationForChat()" :aria-current="currentSection === 'chat' ? 'page' : undefined">
          <span class="nav-icon" aria-hidden="true">◌</span><span>Агент</span>
        </RouterLink>
      </nav>
    </div>
  </main>
</template>
