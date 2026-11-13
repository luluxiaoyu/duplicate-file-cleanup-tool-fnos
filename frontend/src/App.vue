<script setup lang="ts">
import { onMounted } from 'vue'
import { useUi } from './store'
import { initializeHost } from './host'
import ConfirmDialog from './components/ConfirmDialog.vue'
const ui = useUi()
onMounted(initializeHost)
</script>
<template>
  <a class="skip-link" href="#main">跳转到主要内容</a>
  <header class="app-header"><div class="brand"><svg width="30" height="30" viewBox="0 0 32 32" aria-hidden="true"><rect x="5" y="3" width="16" height="22" rx="2" fill="none" stroke="currentColor" stroke-width="1.8"/><rect x="11" y="8" width="16" height="22" rx="2" fill="var(--surface)" stroke="currentColor" stroke-width="1.8"/><path d="m15 19 3 3 5-7" fill="none" stroke="currentColor" stroke-width="1.8"/></svg><span>重复文件清理小工具</span></div><nav aria-label="主导航"><RouterLink to="/">查重</RouterLink><RouterLink to="/tasks">任务记录</RouterLink><RouterLink to="/trash">回收站</RouterLink></nav></header>
  <main id="main"><div v-if="ui.error" role="alert" class="banner error"><span>{{ ui.error }}</span><button aria-label="关闭错误提示" @click="ui.error = ''">关闭</button></div><p v-if="ui.notice" role="status" class="banner">{{ ui.notice }}</p><RouterView /></main>
  <ConfirmDialog />
</template>
