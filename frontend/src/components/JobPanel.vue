<script setup lang="ts">
import type { Job } from '../types'
import { active, bytes, date, estimate, states } from '../api'
defineProps<{ job: Job }>()
</script>
<template>
  <section class="panel" aria-label="任务进度和结果">
    <div class="section-heading"><h2>{{ job.stage }}</h2><span class="tag">{{ states[job.state] || job.state }}</span></div>
    <p class="muted">{{ date(job.createdAt) }} · <span v-if="job.kind === 'scan'">已发现 {{ job.discovered }} 个文件</span><span v-else>已处理 {{ job.processed }} / {{ job.total ?? '待确定' }} 个文件</span></p>
    <progress v-if="active(job.state)" :value="job.currentTotal ? job.currentBytes : job.total && job.state !== 'queued' ? job.processed : undefined" :max="job.currentTotal || job.total || 1" aria-label="当前阶段进度"></progress>
    <p v-if="active(job.state) && job.currentTotal" class="muted">当前文件：{{ bytes(job.currentBytes) }} / {{ bytes(job.currentTotal) }}</p>
    <p v-if="job.error" class="error">{{ job.error }}</p>
    <template v-if="job.kind !== 'scan'">
      <dl class="stats"><div><dt>处理的重复数据</dt><dd>{{ bytes(job.logicalBytes) }}</dd></div><div><dt>空间节省（估算）</dt><dd>{{ estimate(job.estimatedSavedBytes, job.estimateComplete) }}<small v-if="!job.estimateComplete">统计不完整，仅包含已确认部分</small></dd></div><div><dt>已共享／无需处理</dt><dd>{{ bytes(job.alreadySharedBytes) }}</dd></div><div><dt>回收站待清空</dt><dd>{{ bytes(job.trashBytes) }}</dd></div></dl>
      <p class="muted">估算只包含可确认的成功操作。移入回收站尚未释放空间；合并字节数不等于物理释放量。</p>
      <details v-if="job.space.length"><summary>查看各文件系统可用空间变化</summary>
        <div v-for="(space, index) in job.space" :key="space.fileSystemId" class="space-row">
          <strong>文件系统 {{ index + 1 }} · {{ space.fileSystemId.split(':')[0] }}</strong>
          <p v-if="space.error" class="error">{{ space.error }}</p>
          <p v-else>开始 {{ bytes(space.beforeAvailable) }} → 结束 {{ bytes(space.afterAvailable) }}<template v-if="space.delayedAvailable !== null"> → 复测 {{ bytes(space.delayedAvailable) }}</template></p>
          <p>观测变化：{{ (space.delayedAvailable ?? space.afterAvailable) === null ? '等待测量' : bytes((space.delayedAvailable ?? space.afterAvailable)! - space.beforeAvailable) }}</p>
          <p class="muted">{{ date(space.delayedAt || space.afterAt || space.beforeAt) }} · 包含其他应用读写、快照及延迟回收的影响。</p>
        </div>
      </details>
    </template>
    <p v-if="job.errors" class="error">{{ job.errors }} 项未完成或需检查，相关文件不会自动删除。</p>
  </section>
</template>
