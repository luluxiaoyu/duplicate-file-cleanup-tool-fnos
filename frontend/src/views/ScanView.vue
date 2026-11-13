<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '../api'
import { authorizeDirectory } from '../host'
import { useUi } from '../store'
import { confirm } from '../confirm'
import type { Folder, Job } from '../types'
const ui = useUi(), router = useRouter(), folders = ref<Folder[]>([]), selected = ref<string[]>([]), priority = ref('')
const loaded = ref(false)
async function refresh() { folders.value = await api<Folder[]>('/folders'); selected.value = selected.value.filter(p => folders.value.some(f => f.path === p && !f.error)); loaded.value = true }
async function add() { await ui.run(async () => { await authorizeDirectory(); await refresh() }) }
async function revoke(folder: Folder) {
  if (!await confirm('撤销目录授权', `撤销“${folder.displayPath}”后，后台任务将停止访问此目录，回收站恢复也需要重新授权。`, '撤销授权')) return
  await ui.run(async () => { await api('/folders/revoke', { path: folder.path }); await refresh() })
}
async function scan() {
  await ui.run(async () => {
    const task = await api<Job>('/scans', { roots: selected.value, priorityRoots: priority.value ? [priority.value] : [], idempotencyKey: crypto.randomUUID() })
    await router.push(`/tasks/${task.id}`)
  })
}
onMounted(() => ui.run(refresh))
</script>
<template>
  <div class="page-heading"><div><p class="eyebrow">01 / 查重</p><h1>选择要检查的目录</h1><p class="muted">只识别完整内容相同的文件，扫描不会修改文件。</p></div><button :disabled="ui.busy" @click="add">添加授权目录</button></div>
  <section class="panel"><div class="section-heading"><h2>已授权目录</h2><button class="text-button" :disabled="ui.busy" @click="ui.run(refresh)">刷新授权状态</button></div>
    <p v-if="!loaded" role="status">正在读取授权目录…</p>
    <div v-else-if="!folders.length" class="empty"><h3>还没有授权目录</h3><p>添加需要查重的文件夹，可以选择多个目录一起比较。</p><button class="primary" :disabled="ui.busy" @click="add">选择文件夹</button></div>
    <ul v-else class="folder-list"><li v-for="folder in folders" :key="folder.path"><label class="folder-choice"><input v-model="selected" type="checkbox" :value="folder.path" :disabled="!!folder.error"><span><strong class="path">{{ folder.displayPath }}</strong><span class="muted block">{{ folder.fileSystem?.type || '类型未知' }}<template v-if="folder.fileSystem?.readOnly"> · 只读</template><template v-if="folder.fileSystem?.btrfs && !folder.fileSystem.readOnly"> · 可尝试 Btrfs 合并</template><template v-else-if="folder.fileSystem?.hardlinkCandidate"> · 可尝试硬链接</template></span><span v-if="folder.error" class="error block">{{ folder.error }}</span></span></label><button class="text-button" :disabled="ui.busy" @click="revoke(folder)">撤销授权</button></li></ul>
    <div v-if="folders.length" class="form-row"><label for="priority">优先保留目录</label><select id="priority" v-model="priority"><option value="">按选择顺序和路径排序</option><option v-for="folder in folders.filter(f => selected.includes(f.path))" :key="folder.path" :value="folder.path">{{ folder.displayPath }}</option></select></div>
    <div class="panel-footer"><span class="muted">已选择 {{ selected.length }} 个目录</span><button class="primary" :disabled="ui.busy || !selected.length" @click="scan">{{ ui.busy ? '正在准备…' : '开始查重' }}</button></div>
  </section>
  <p class="footnote">扫描会识别已有硬链接和共享区块。是否可清理、预计能节省多少空间，会在结果中分别显示。</p>
</template>
