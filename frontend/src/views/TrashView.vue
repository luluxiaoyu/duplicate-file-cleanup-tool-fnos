<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api, bytes } from '../api'
import type { TrashItem, Job } from '../types'
import { useUi } from '../store'
import { confirm } from '../confirm'
const ui = useUi(), router = useRouter(), items = ref<TrashItem[]>([]), selected = ref<string[]>([]), after = ref(0), hasMore = ref(false), loaded = ref(false)
const key = (x: TrashItem) => `${x.taskId}:${x.operationId}`
const currentAfter = ref(0)
async function load(next = false) { const start = next ? after.value : 0; const page = await api<{ items: TrashItem[]; nextOffset: number }>(`/trash?after=${start}`); items.value = page.items; selected.value = []; after.value = page.nextOffset; currentAfter.value = start; hasMore.value = after.value - start >= 20; loaded.value = true }
async function act(kind: string) {
  const chosen = items.value.filter(x => selected.value.includes(key(x)))
  if (!chosen.length || !await confirm(kind === 'purge' ? '永久清空选中文件' : '恢复选中文件', kind === 'purge' ? `将永久删除 ${chosen.length} 个文件，此操作不可恢复。` : `将恢复 ${chosen.length} 个文件到原路径，不覆盖同名文件。`, kind === 'purge' ? '永久清空' : '恢复文件', kind === 'purge')) return
  await ui.run(async () => { const job = await api<Job>('/trash/tasks', { kind, items: chosen.map(x => ({ taskId: x.taskId, operationId: x.operationId })), confirmed: true, idempotencyKey: crypto.randomUUID() }); await router.push(`/tasks/${job.id}`) })
}
onMounted(() => ui.run(() => load()))
</script>
<template>
  <div class="page-heading"><div><h1>应用回收站</h1><p class="muted">这里的文件仍占用空间。永久清空后才可能释放空间。</p></div><button :disabled="ui.busy" @click="ui.run(() => load())">刷新</button></div>
  <section class="panel"><p v-if="!loaded" role="status">正在读取回收站…</p><div v-else-if="!items.length" class="empty"><h2>当前没有可访问的回收站文件</h2><p>已撤销授权的目录需要重新授权后才能查看和恢复。</p></div>
    <ul v-else class="folder-list"><li v-for="item in items" :key="key(item)"><label class="folder-choice"><input v-model="selected" type="checkbox" :value="key(item)"><span class="path">{{ item.displayPath }}</span></label><span class="numeric">{{ bytes(item.size) }}</span></li></ul>
    <div v-if="items.length" class="panel-footer"><label class="check-row"><input :checked="selected.length === items.length" type="checkbox" @change="selected = ($event.target as HTMLInputElement).checked ? items.map(key) : []">选择本页全部</label><div class="actions"><button :disabled="ui.busy || !selected.length" @click="act('restore')">恢复文件</button><button class="danger" :disabled="ui.busy || !selected.length" @click="act('purge')">永久清空</button></div></div>
  </section><div v-if="hasMore || currentAfter" class="pagination"><button :disabled="ui.busy" @click="ui.run(() => load())">回到首批</button><button v-if="hasMore" :disabled="ui.busy" @click="ui.run(() => load(true))">下一批文件</button></div>
</template>
