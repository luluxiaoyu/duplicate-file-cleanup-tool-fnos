import { reactive } from 'vue'
export const confirmation = reactive({ title: '', message: '', button: '确认', open: false, dangerous: false })
let resolve: ((value: boolean) => void) | undefined
export function confirm(title: string, message: string, button: string, dangerous = false): Promise<boolean> {
  if (resolve) return Promise.resolve(false)
  Object.assign(confirmation, { title, message, button, open: true, dangerous })
  return new Promise(done => { resolve = done })
}
export function finishConfirmation(value: boolean) { confirmation.open = false; resolve?.(value); resolve = undefined }
