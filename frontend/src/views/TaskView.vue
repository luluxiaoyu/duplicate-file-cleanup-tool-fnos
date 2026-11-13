<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { active, api, bytes, estimate, kinds, sharingLabels } from '../api'
import type { Choice, Cleanup, FileView, Group, Job, Preview } from '../types'
import { useUi } from '../store'
import { confirm } from '../confirm'
import JobPanel from '../components/JobPanel.vue'
const route = useRoute(), router = useRouter(), ui = useUi()
const job = ref<Job>(), groups = ref<Group[]>([]), after = ref(0), hasMore = ref(false), loadedGroups = ref(false)
const method = ref('dedupe'), allGroups = ref(true), choices = ref<Record<number, Choice>>({}), preview = ref<Preview>(), priorityRoots = ref<string[]>([])
const issues = ref<{ id: string; displayPath: string; message: string; recoveryPath: string | null }[]>([])
let timer: ReturnType<typeof setTimeout> | undefined, generation = 0, alive = true
const methods = [ { id: 'dedupe', label: 'Btrfs 区块合并', detail: '保留路径，文件仍可独立修改' }, { id: 'trash', label: '移入应用回收站', detail: '可恢复，清空前不释放空间' }, { id: 'delete', label: '永久删除', detail: '删除多余路径，不可恢复' }, { id: 'hardlink', label: '硬链接替换', detail: '保留路径，共用内容和元数据' } ]
const ready = computed(() => job.value?.kind === 'scan' && ['completed', 'completedWithErrors'].includes(job.value.state))
const request = (): Cleanup => ({ scanId: job.value!.id, method: method.value, allGroups: allGroups.value, choices: Object.values(choices.value), priorityRoots: priorityRoots.value, confirmed: false, idempotencyKey: crypto.randomUUID() })
function choice(group: Group) { return choices.value[group.id] ??= { groupId: group.id, keeperId: group.keeperId, fileIds: null, exclude: false } }
function includeFile(group: Group, id: number, include: boolean) {
  const c = choice(group), ids = new Set(c.excludedFileIds || [])
  if (include) ids.delete(id); else ids.add(id)
  c.excludedFileIds = [...ids]; void ui.run(save)
}
function disabledMethod(id: string) {
  const files = groups.value.flatMap(g => g.files)
  return files.length > 0 && ((id === 'dedupe' && files.every(f => !f.fileSystem?.btrfs || f.fileSystem.readOnly)) || (id === 'hardlink' && files.every(f => !f.fileSystem?.hardlinkCandidate)))
}
async function save() { preview.value = undefined; await api(`/scans/${job.value!.id}/draft`, request()) }
async function loadGroups(append = false) {
  const page = await api<{ groups: Group[]; nextGroupId: number; hasMore: boolean }>(`/scans/${job.value!.id}/groups?after=${append ? after.value : 0}`)
  groups.value = page.groups; after.value = page.nextGroupId; hasMore.value = page.hasMore; loadedGroups.value = true
}
async function moreFiles(group: Group, first = false) {
  const start = first ? 0 : group.nextFileId
  const page = await api<{ files: FileView[]; nextFileId: number; hasMore: boolean }>(`/scans/${job.value!.id}/groups/${group.id}/files?after=${start}`)
  // Replace the member page, keeping the result bounded even for an enormous duplicate group.
  group.files = page.files; group.nextFileId = page.nextFileId; group.hasMoreFiles = page.hasMore; group.pageAfter = start
}
async function poll(version: number) {
  try {
    const value = await api<Job>(`/tasks/${String(route.params.id)}`)
    if (version !== generation || !alive) return
    job.value = value
    if (value.errors && !active(value.state)) issues.value = (await api<{ issues: typeof issues.value }>(`/tasks/${value.id}/issues`)).issues
    if (ready.value && !loadedGroups.value) {
      const draft = await api<Cleanup | null>(`/scans/${value.id}/draft`)
      if (draft) { method.value = draft.method; allGroups.value = draft.allGroups; choices.value = Object.fromEntries(draft.choices.map(c => [c.groupId, c])); priorityRoots.value = draft.priorityRoots }
      await loadGroups()
    }
  } catch (e) { ui.error = e instanceof Error ? e.message : '读取任务失败' }
  finally { if (version === generation && alive && (!job.value || active(job.value.state))) timer = setTimeout(() => poll(version), 1000) }
}
watch(() => route.params.id, () => { clearTimeout(timer); generation++; job.value = undefined; groups.value = []; loadedGroups.value = false; choices.value = {}; preview.value = undefined; after.value = 0; void poll(generation) }, { immediate: true })
onUnmounted(() => { alive = false; generation++; clearTimeout(timer) })
async function control(action: string) { await ui.run(async () => { await api(`/tasks/${job.value!.id}/${action}`, {}); clearTimeout(timer); void poll(generation) }) }
async function inspect() { await ui.run(async () => { await save(); preview.value = await api<Preview>('/cleanups/preview', request()) }) }
async function clean() {
  if (!preview.value || !preview.value.files) return
  const details = method.value === 'hardlink' ? '\n硬链接后的路径共享内容和元数据，修改任一路径可能影响其他路径。' : method.value === 'delete' ? '\n永久删除不可恢复。' : method.value === 'trash' ? '\n这些文件在永久清空回收站前仍占用空间。' : '\n文件路径保留，数据区块合并后仍可独立修改。'
  if (!await confirm('确认清理', `将处理 ${preview.value.files} 个文件，逻辑大小 ${bytes(preview.value.logicalBytes)}。${details}`, '执行清理', ['delete', 'hardlink'].includes(method.value))) return
  await ui.run(async () => { const created = await api<Job>('/cleanups', { ...request(), confirmed: true }); await router.push(`/tasks/${created.id}`) })
}
</script>
<template>
  <div class="page-heading"><div><RouterLink to="/tasks" class="back-link">← 任务记录</RouterLink><h1>{{ job ? kinds[job.kind] : '任务' }}详情</h1></div><div v-if="job" class="actions"><button v-if="active(job.state)" :disabled="ui.busy" @click="control('cancel')">取消任务</button><button v-if="job.state === 'interrupted'" class="primary" :disabled="ui.busy" @click="control('resume')">继续任务</button></div></div>
  <p v-if="!job" role="status">正在读取任务…</p><JobPanel v-else :job="job" />
  <section v-if="issues.length" class="panel"><h2>未完成的操作</h2><ul class="folder-list"><li v-for="issue in issues" :key="issue.id"><div class="path"><strong>{{ issue.displayPath }}</strong><p class="error">{{ issue.message }}</p><p v-if="issue.recoveryPath" class="muted">暂存位置：{{ issue.recoveryPath }}</p></div></li></ul></section>
  <fieldset v-if="ready" :disabled="ui.busy" class="result-controls"><legend class="sr-only">查重结果和清理选项</legend>
    <section class="panel"><div class="section-heading"><div><p class="eyebrow">02 / 清理</p><h2>选择处理方式</h2></div></div>
      <fieldset class="method-grid"><legend class="sr-only">清理方式</legend><label v-for="item in methods" :key="item.id" :class="['method', { chosen: method === item.id, unavailable: disabledMethod(item.id) }]"><input v-model="method" type="radio" name="method" :value="item.id" :disabled="disabledMethod(item.id)" @change="ui.run(save)"><span><strong>{{ item.label }}</strong><small>{{ disabledMethod(item.id) ? '当前结果不支持此方式' : item.detail }}</small></span></label></fieldset>
      <label class="check-row"><input v-model="allGroups" type="checkbox" @change="ui.run(save)">处理所有结果页中的重复组（保留各组的建议文件）</label>
      <p class="muted">可逐组修改保留项或排除整组。Btrfs 和硬链接会在每个兼容文件系统内各保留一个参考文件。</p>
    </section>
    <p v-if="loadedGroups && !groups.length" class="empty">当前结果没有重复文件。</p>
    <section v-for="group in groups" :key="group.id" class="panel duplicate-group">
      <div class="section-heading"><label class="check-row"><input :checked="!choices[group.id]?.exclude && (allGroups || !!choices[group.id])" type="checkbox" @change="choice(group).exclude = !($event.target as HTMLInputElement).checked; ui.run(save)"><strong>重复组 {{ group.id }}</strong></label><span class="muted">{{ group.count }} 个路径 · 每份 {{ bytes(group.size) }}</span></div>
      <div class="table-wrapper" tabindex="0" role="region" :aria-label="`重复组 ${group.id} 的文件`"><table><thead><tr><th scope="col">保留／清理</th><th scope="col">文件路径</th><th scope="col">文件系统</th><th scope="col">空间状态</th></tr></thead><tbody><tr v-for="file in group.files" :key="file.id"><td data-label="保留／清理"><label class="check-row"><input :checked="(choices[group.id]?.keeperId || group.keeperId) === file.id" type="radio" :name="`keeper-${group.id}`" :aria-label="`保留 ${file.displayPath}`" @change="choice(group).keeperId = file.id; ui.run(save)"><small>保留</small></label><label v-if="(choices[group.id]?.keeperId || group.keeperId) !== file.id" class="check-row"><input type="checkbox" :checked="!choices[group.id]?.excludedFileIds?.includes(file.id)" :aria-label="`清理 ${file.displayPath}`" @change="includeFile(group, file.id, ($event.target as HTMLInputElement).checked)"><small>清理</small></label></td><th scope="row" class="path">{{ file.displayPath }}</th><td data-label="文件系统">{{ file.fileSystem?.type || '未知' }}</td><td data-label="空间状态"><span class="tag">{{ sharingLabels[file.sharing] }}</span><small v-if="file.sharedBytes" class="block muted">与参考文件共享 {{ bytes(file.sharedBytes) }}</small><small v-if="file.exclusiveBytes !== null" class="block muted">额外独占数据 {{ bytes(file.exclusiveBytes) }}</small><small v-if="file.reason" class="block muted">{{ file.reason }}</small></td></tr></tbody></table></div>
      <div v-if="group.hasMoreFiles || group.pageAfter" class="actions member-pages"><button v-if="group.pageAfter" :disabled="ui.busy" @click="ui.run(() => moreFiles(group, true))">回到首批文件</button><button v-if="group.hasMoreFiles" :disabled="ui.busy" @click="ui.run(() => moreFiles(group))">下一页文件</button><span class="muted">保留选择会随任务保存</span></div>
    </section>
    <div v-if="groups.length" class="pagination"><button :disabled="ui.busy || after <= 20" @click="after = 0; ui.run(() => loadGroups())">回到首批重复组</button><button v-if="hasMore" :disabled="ui.busy" @click="ui.run(() => loadGroups(true))">下一批重复组</button></div>
    <section v-if="preview" class="panel" aria-label="清理预览"><h2>清理预览</h2><dl class="stats"><div><dt>将处理</dt><dd>{{ preview.files }} 个文件</dd></div><div><dt>逻辑重复大小</dt><dd>{{ bytes(preview.logicalBytes) }}</dd></div><div><dt>预计节省空间</dt><dd>{{ estimate(preview.estimatedSavedBytes, preview.estimateComplete) }}<small v-if="!preview.estimateComplete">统计不完整</small></dd></div><div><dt>已共享／无需处理</dt><dd>{{ bytes(preview.alreadySharedBytes) }}</dd></div></dl><p v-if="method === 'trash'" class="muted">移入回收站不释放空间。</p><p v-if="preview.unavailable" class="error">{{ preview.unavailable }} 个文件不可处理：{{ preview.reasons.join('；') }}</p></section>
    <div v-if="groups.length" class="cleanup-bar"><span class="muted">文件操作前会再次检查内容、授权和权限。</span><div class="actions"><button :disabled="ui.busy" @click="inspect">{{ ui.busy ? '正在检查…' : '检查清理预览' }}</button><button class="primary" :disabled="ui.busy || !preview?.files" @click="clean">执行清理</button></div></div>
  </fieldset>
</template>
