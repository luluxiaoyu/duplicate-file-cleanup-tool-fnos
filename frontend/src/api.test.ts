import { afterEach, describe, expect, it, vi } from 'vitest'
import { api, APP, estimate } from './api'
afterEach(() => vi.unstubAllGlobals())
describe('gateway API client', () => {
  it('propagates permission errors without treating them as empty results', async () => {
    vi.stubGlobal('document', { documentElement: { lang: 'zh-CN' } })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ error: '目录授权已撤销' }), { status: 403 })))
    await expect(api('/scans', { roots: ['/authorized'] })).rejects.toThrow('目录授权已撤销')
    expect(fetch).toHaveBeenCalledWith(expect.any(String), expect.objectContaining({ credentials: 'same-origin', headers: expect.objectContaining({ 'X-Requested-With': APP }) }))
  })
  it('handles non-JSON gateway login failures', async () => {
    vi.stubGlobal('document', { documentElement: { lang: 'zh-CN' } })
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>login</html>', { status: 401 })))
    await expect(api('/folders')).rejects.toThrow('请求失败')
  })
  it('never presents wholly unknown savings as zero', () => {
    vi.stubGlobal('document', { documentElement: { lang: 'zh-CN' } })
    expect(estimate(0, false)).toBe('无法确定')
    expect(estimate(0, true)).toBe('0 B')
    expect(estimate(1024, false)).toBe('1 KiB')
  })
})
