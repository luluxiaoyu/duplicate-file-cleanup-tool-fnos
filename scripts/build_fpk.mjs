import { execFileSync } from 'node:child_process'
import { createHash } from 'node:crypto'
import { cpSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync, chmodSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { tmpdir } from 'node:os'
import { fileURLToPath } from 'node:url'
import assert from 'node:assert/strict'
const root = fileURLToPath(new URL('../', import.meta.url))
const arch = process.argv[2]
assert.ok(['amd64', 'arm64'].includes(arch), 'Usage: node scripts/build_fpk.mjs amd64|arm64 <AOT publish directory>')
const publish = resolve(process.argv[3] || join(root, '.build', arch))
const manifest = new Map(readFileSync(join(root, 'manifest'), 'utf8').trim().split('\n').map(line => { const i = line.indexOf('='); return [line.slice(0, i), line.slice(i + 1)] }))
assert.equal(JSON.parse(readFileSync(join(root, 'config/privilege'), 'utf8')).defaults['run-as'], 'package')
assert.deepEqual(JSON.parse(readFileSync(join(root, 'config/resource'), 'utf8'))['api-scope'].sort(), ['trim.file.path', 'trim.file.userAccess', 'trim.file.userAcl'])
const entry = JSON.parse(readFileSync(join(root, 'app/ui/config'), 'utf8'))['.url'][manifest.get('desktop_applaunchname')]
assert.equal(entry.gatewayPrefix, '/app/' + manifest.get('appname')); assert.equal(entry.gatewaySocket, 'app.sock')
for (const name of ['main', 'install_init', 'install_callback', 'upgrade_init', 'upgrade_callback', 'config_init', 'config_callback', 'uninstall_init', 'uninstall_callback']) assert.ok(statSync(join(root, 'cmd', name)).mode & 0o111, `cmd/${name} must be executable`)
const version = (process.env.APP_VERSION || manifest.get('version')).replace(/^v/, '')
assert.match(version, /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-\d+)?$/)
const binary = readFileSync(join(publish, 'duplicate-cleanup'))
assert.ok(!existsSync(join(publish, 'duplicate-cleanup.dll')) && !existsSync(join(publish, 'duplicate-cleanup.runtimeconfig.json')), 'Expected Native AOT output, not a managed apphost publish')
assert.equal(binary.subarray(0, 4).toString('hex'), '7f454c46', 'Expected a Linux AOT ELF binary')
assert.equal(binary[4], 2); assert.equal(binary[5], 1)
assert.equal(binary.readUInt16LE(18), arch === 'amd64' ? 62 : 183)
const nativeSqlite = join(publish, 'libe_sqlite3.so')
const sqlite = readFileSync(nativeSqlite)
assert.equal(sqlite.readUInt16LE(18), arch === 'amd64' ? 62 : 183, 'SQLite architecture mismatch')
assert.ok(statSync(join(root, 'frontend/dist/index.html')).size)
const work = mkdtempSync(join(tmpdir(), 'duplicate-fpk-'))
const run = (cmd, args) => execFileSync(cmd, args, { stdio: 'inherit', env: { ...process.env, COPYFILE_DISABLE: '1' } })
try {
  const packageRoot = join(work, 'package'), payload = join(work, 'payload')
  mkdirSync(join(payload, 'bin'), { recursive: true }); mkdirSync(packageRoot)
  for (const name of ['duplicate-cleanup', 'libe_sqlite3.so']) { copyFileSync(join(publish, name), join(payload, 'bin', name)); chmodSync(join(payload, 'bin', name), 0o755) }
  cpSync(join(root, 'frontend/dist'), join(payload, 'www'), { recursive: true })
  cpSync(join(root, 'app/ui'), join(payload, 'ui'), { recursive: true })
  run('tar', ['--format=ustar', '-czf', join(packageRoot, 'app.tgz'), '-C', payload, 'bin', 'www', 'ui'])
  for (const name of ['cmd', 'config', 'wizard']) cpSync(join(root, name), join(packageRoot, name), { recursive: true })
  cpSync(join(root, 'app/ui'), join(packageRoot, 'ui'), { recursive: true })
  for (const name of ['ICON.PNG', 'ICON_256.PNG']) copyFileSync(join(root, name), join(packageRoot, name))
  manifest.set('platform', arch === 'amd64' ? 'x86' : 'arm'); manifest.set('version', version)
  manifest.set('checksum', createHash('md5').update(readFileSync(join(packageRoot, 'app.tgz'))).digest('hex'))
  writeFileSync(join(packageRoot, 'manifest'), Array.from(manifest, ([k, v]) => `${k}=${v}`).join('\n') + '\n')
  const output = join(root, 'dist_fpk'); mkdirSync(output, { recursive: true })
  const result = join(output, `${manifest.get('appname')}_${version}_${arch}.fpk`)
  run('tar', ['--format=ustar', '-czf', result, '-C', packageRoot, 'manifest', 'app.tgz', 'cmd', 'config', 'wizard', 'ui', 'ICON.PNG', 'ICON_256.PNG'])
  writeFileSync(result + '.sha256', createHash('sha256').update(readFileSync(result)).digest('hex') + '  ' + result.split('/').at(-1) + '\n')
  console.log(result)
} finally { rmSync(work, { recursive: true, force: true }) }
