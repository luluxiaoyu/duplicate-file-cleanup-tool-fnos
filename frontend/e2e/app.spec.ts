import { test, expect } from '@playwright/test'
const taskId = 'a'.repeat(32)
const base = '/app/duplicate-file-cleanup-tool-fnos'
const fs = { type: 'Btrfs', identity: 'Btrfs:test', mountId: 1, subvolumeId: 5, readOnly: false, btrfs: true, hardlinkCandidate: true, reason: null }
const job = { id: taskId, owner: 1000, kind: 'scan', state: 'completed', stage: '已完成', createdAt: '2026-11-13T07:00:00Z', updatedAt: '2026-11-13T07:01:00Z', scanId: null, method: null, discovered: 300, processed: 300, total: 300, logicalBytes: 0, mergedBytes: 0, alreadySharedBytes: 0, estimatedSavedBytes: 0, estimateComplete: true, trashBytes: 0, errors: 0, error: null, space: [] }
test.beforeEach(async ({ page }) => {
  await page.route(`**${base}/api/**`, async route => {
    const path = new URL(route.request().url()).pathname.slice(`${base}/api`.length)
    let value: unknown = {}
    if (path === '/folders') value = [{ path: '/vol1/test', displayPath: '我的文件/资料', fileSystem: fs, error: null }]
    else if (path === '/scans') value = job
    else if (path === '/tasks') value = { tasks: [job], nextOffset: 1 }
    else if (path === `/tasks/${taskId}`) value = job
    else if (path.endsWith('/draft')) value = null
    else if (path.endsWith('/groups')) value = { groups: [{ id: 1, keeperId: 1, count: 2, size: 1073741824, nextFileId: 2, files: [
      { id: 1, displayPath: '我的文件/资料/原始文件.iso', size: 1073741824, links: 1, sharing: 'reference', sharedBytes: 0, exclusiveBytes: 1073741824, fileSystem: fs, reason: null },
      { id: 2, displayPath: `我的文件/${'很长的目录名称/'.repeat(12)}副本.iso`, size: 1073741824, links: 1, sharing: 'partlyShared', sharedBytes: 536870912, exclusiveBytes: 536870912, fileSystem: fs, reason: '存在其他共享引用，可能包含快照' },
    ] }], nextGroupId: 1 }
    else if (path === '/cleanups/preview') value = { files: 1, logicalBytes: 1073741824, alreadySharedBytes: 536870912, estimatedSavedBytes: 536870912, estimateComplete: false, unavailable: 0, reasons: [] }
    else if (path === '/trash') value = { items: [{ taskId, operationId: 'op', displayPath: '我的文件/资料/副本.iso', size: 1024, state: 'trashed', error: null }], nextOffset: 1 }
    await route.fulfill({ json: value })
  })
})
test('scan → results → permanent delete requires explicit confirmation', async ({ page }) => {
  let destructive = 0
  page.on('request', request => { if (request.url().endsWith('/api/cleanups')) destructive++ })
  await page.goto('./')
  await expect(page.getByText('我的文件/资料', { exact: true })).toBeVisible()
  await page.getByLabel('我的文件/资料').check()
  await page.getByRole('button', { name: '开始查重', exact: true }).click()
  await expect(page.getByText('部分区块共享', { exact: true })).toBeVisible()
  await page.getByLabel('永久删除', { exact: false }).check()
  await page.getByRole('button', { name: '检查清理预览', exact: true }).click()
  await expect(page.getByText('统计不完整', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '执行清理', exact: true }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  await expect(page.getByRole('dialog')).toContainText('永久删除不可恢复')
  await page.getByRole('button', { name: '取消', exact: true }).click()
  expect(destructive).toBe(0)
  await expect(page.getByRole('dialog')).not.toBeVisible()
})
test('narrow dark result layout handles long paths without page overflow', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.emulateMedia({ colorScheme: 'dark' })
  await page.goto(`./tasks/${taskId}`)
  await expect(page.getByText('部分区块共享', { exact: true })).toBeVisible()
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: '../docs/ui-mobile-dark.png', fullPage: true })
})
test('recycle bin does not claim space was released and protects purge', async ({ page }) => {
  await page.goto('./trash')
  await expect(page.getByText('这里的文件仍占用空间。永久清空后才可能释放空间。')).toBeVisible()
  await page.getByLabel('我的文件/资料/副本.iso').check()
  await page.getByRole('button', { name: '永久清空', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('此操作不可恢复')
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).not.toBeVisible()
})
