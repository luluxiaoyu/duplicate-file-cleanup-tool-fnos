export const APP = 'duplicate-file-cleanup-tool-fnos'
export const BASE = `/app/${APP}`
export async function api<T>(path: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const response = await fetch(`${BASE}/api${path}`, {
    method: body === undefined ? 'GET' : 'POST', credentials: 'same-origin', signal,
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': APP, 'X-App-Language': document.documentElement.lang || 'zh-CN' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!response.ok) {
    const result = await response.json().catch(() => ({})) as { error?: string }
    throw new Error(result.error || '请求失败，请检查登录状态或稍后重试')
  }
  return response.json() as Promise<T>
}
export const bytes = (value: number | null | undefined): string => {
  if (value == null) return '无法确定'
  const sign = value < 0 ? '−' : ''
  let n = Math.abs(value), unit = 0
  while (n >= 1024 && unit < 5) { n /= 1024; unit++ }
  return `${sign}${new Intl.NumberFormat(document.documentElement.lang, { maximumFractionDigits: unit ? 2 : 0 }).format(n)} ${['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB'][unit]}`
}
export const date = (value: string) => new Intl.DateTimeFormat(document.documentElement.lang, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
export const estimate = (value: number, complete: boolean) => !complete && value === 0 ? '无法确定' : bytes(value)
export const active = (state: string) => ['queued', 'running', 'cancelling'].includes(state)
export const states: Record<string, string> = { queued: '排队中', running: '进行中', cancelling: '正在停止', interrupted: '已中断', completed: '已完成', completedWithErrors: '部分完成', cancelled: '已取消' }
export const kinds: Record<string, string> = { scan: '查重', cleanup: '清理', restore: '恢复文件', purge: '清空回收站' }
export const sharingLabels: Record<string, string> = { reference: '参考文件', hardlink: '已有硬链接', fullyShared: '数据已全部共享', partlyShared: '部分区块共享', independent: '存在独立区块', noAllocatedData: '未分配数据区块', unknown: '空间状态未确定', missing: '路径已不存在', changed: '文件已变化' }
