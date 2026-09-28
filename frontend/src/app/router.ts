import { createRouter, createWebHistory } from 'vue-router'
import AppPage from './AppPage.vue'
import { readViewState } from '../shared/uiViewState'

const savedPath = readViewState('activeRoute', '/knowledge')
const startPath = typeof savedPath === 'string' && /^\/(knowledge|planning|tasks|chat|search|testing)(\/|\?|$)/.test(savedPath)
  ? savedPath : '/knowledge'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: startPath },
    { path: '/knowledge/:pathMatch(.*)*', component: AppPage, props: { section: 'knowledge' } },
        { path: '/planning/projects/:projectId/milestones/:milestoneId/features/:featureId', component: AppPage, props: { section: 'planning' } },
        { path: '/planning/projects/:projectId/milestones/:milestoneId', component: AppPage, props: { section: 'planning' } },
        { path: '/planning/projects/:projectId', component: AppPage, props: { section: 'planning' } },
        { path: '/planning/:pathMatch(.*)*', component: AppPage, props: { section: 'planning' } },
        { path: '/tasks/:taskId', component: AppPage, props: { section: 'tasks' } },
        { path: '/tasks/:pathMatch(.*)*', component: AppPage, props: { section: 'tasks' } },
    { path: '/testing', component: AppPage, props: { section: 'testing' } },
    { path: '/chat', component: AppPage, props: { section: 'chat' } },
    { path: '/search', component: AppPage, props: { section: 'search' } },
    { path: '/:pathMatch(.*)*', redirect: '/knowledge' },
  ],
})
