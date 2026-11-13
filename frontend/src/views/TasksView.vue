<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { api, date, kinds, states } from '../api'
import type { Job } from '../types'
import { useUi } from '../store'
const ui = useUi(), jobs = ref<Job[]>([]), offset = ref(0), hasMore = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined, alive = true
async function load(append = false) { const page = await api<{ tasks: Job[]; nextOffset: number; hasMore: boolean }>(`/tasks?after=${append ? offset.value : 0}`); jobs.value = page.tasks; offset.value = page.nextOffset; hasMore.value = page.hasMore }
async function poll() { try { if (offset.value <= 20) await load() } catch (e) { ui.error = e instanceof Error ? e.message : '读取任务失败' } finally { if (alive) timer = setTimeout(poll, 2000) } }
onMounted(poll); onUnmounted(() => { alive = false; clearTimeout(timer) })
</script>
<template>
  <div class="page-heading"><div><h1>任务记录</h1><p class="muted">任务在后台运行。应用或 NAS 重启后，可在这里手动继续。</p></div><RouterLink class="button primary" to="/">新建查重</RouterLink></div>
  <section class="panel"><div v-if="!jobs.length" class="empty"><h2>暂无任务</h2><p>开始一次查重后，进度与结果会保存在这里。</p></div>
    <div v-else class="table-wrapper" tabindex="0" role="region" aria-label="历史任务列表"><table><thead><tr><th scope="col">任务</th><th scope="col">创建时间</th><th scope="col">状态</th><th scope="col">进度</th></tr></thead><tbody><tr v-for="job in jobs" :key="job.id"><th scope="row"><RouterLink :to="`/tasks/${job.id}`">{{ kinds[job.kind] }} · {{ job.id.slice(0, 8) }}</RouterLink></th><td data-label="创建时间">{{ date(job.createdAt) }}</td><td data-label="状态"><span class="tag">{{ states[job.state] || job.state }}</span></td><td data-label="进度">{{ job.stage }}</td></tr></tbody></table></div>
    <div v-if="hasMore || offset > 20" class="panel-footer"><button v-if="offset > 20" :disabled="ui.busy" @click="ui.run(() => load())">回到首批任务</button><button v-if="hasMore" :disabled="ui.busy" @click="ui.run(() => load(true))">下一页任务</button></div>
  </section>
</template>
