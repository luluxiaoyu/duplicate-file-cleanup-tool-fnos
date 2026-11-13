import { TrimApp } from '@trimjs/web-app'
import { APP, BASE } from './api'
export const sdk = new TrimApp()
const stateKey = `${APP}:auth-state`
export async function initializeHost() {
  // Paint a usable theme immediately; a standalone page may wait for host discovery.
  document.documentElement.dataset.theme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  try {
    const config = await sdk.getPlatformConfig()
    document.documentElement.lang = config.language || 'zh-CN'
    document.documentElement.dataset.theme = sdk.isStandaloneWeb ? (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light') : config.theme
    if (sdk.isWeb && !sdk.isStandaloneWeb) {
      await sdk.$on('os/theme', theme => { document.documentElement.dataset.theme = theme })
      await sdk.$on('os/language', lang => { document.documentElement.lang = lang })
    }
  } catch { document.documentElement.dataset.theme = matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light' }
}
export async function authorizeDirectory() {
  if (sdk.isStandaloneWeb) {
    const state = crypto.randomUUID()
    // sessionStorage survives _self navigation, avoiding unreliable window.opener behavior.
    sessionStorage.setItem(stateKey, state)
    await sdk.openAppAuth('pickUserFile', { appName: APP, directory: true, redirectUri: `${BASE}/callback.html`, state }, { target: '_self' })
    return
  }
  const result = await sdk.pickUserFile({ directory: true, title: '选择查重目录', okText: '授权并添加' })
  if (result && result.code !== 0) throw new Error(result.msg || '目录授权未完成')
}
export function consumeState(state: unknown) {
  const expected = sessionStorage.getItem(stateKey)
  sessionStorage.removeItem(stateKey)
  return typeof state === 'string' && !!expected && state === expected
}
