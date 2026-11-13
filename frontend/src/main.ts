import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { createRouter, createWebHistory } from 'vue-router'
import { BASE } from './api'
import App from './App.vue'
import ScanView from './views/ScanView.vue'
import TasksView from './views/TasksView.vue'
import TaskView from './views/TaskView.vue'
import TrashView from './views/TrashView.vue'
import './style.css'
const router = createRouter({ history: createWebHistory(BASE), routes: [
  { path: '/', component: ScanView }, { path: '/tasks', component: TasksView },
  { path: '/tasks/:id', component: TaskView }, { path: '/trash', component: TrashView },
] })
createApp(App).use(createPinia()).use(router).mount('#app')
