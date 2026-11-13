import { sdk, consumeState } from './host'
import { APP, BASE } from './api'
const status = document.getElementById('status')!
try {
  const result = sdk.parseAppAuthCallback(location.href)
  if (!consumeState(result?.state)) throw new Error('授权回调校验失败，请返回应用重新授权。')
  history.replaceState(null, '', `${BASE}/callback.html`)
  if (result.appName && result.appName !== APP || result.method && result.method !== 'pickUserFile') throw new Error('授权返回的应用或操作不匹配，请返回应用重试。')
  if (result.status !== 'success') throw new Error(result.status === 'cancel' ? '已取消授权，可以返回应用重新选择目录。' : '目录授权未完成，请返回应用检查权限。')
  status.textContent = '授权流程已返回，正在刷新目录…'
  location.replace(`${BASE}/?authorized=1`)
} catch (error) { status.textContent = error instanceof Error ? error.message : '授权失败，请返回应用重试。' }
