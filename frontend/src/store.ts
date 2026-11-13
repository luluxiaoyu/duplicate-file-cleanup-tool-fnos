import { defineStore } from 'pinia'
import { ref } from 'vue'
export const useUi = defineStore('ui', () => {
  const error = ref(''), notice = ref(''), busy = ref(false)
  async function run<T>(action: () => Promise<T>): Promise<T | undefined> {
    if (busy.value) return
    busy.value = true; error.value = ''
    try { return await action() } catch (e) { error.value = e instanceof Error ? e.message : '操作失败' }
    finally { busy.value = false }
  }
  return { error, notice, busy, run }
})
