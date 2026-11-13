// npm install --prefix /tmp/duplicate-icon-tools @resvg/resvg-js@2.6.2
// node scripts/render_icons.mjs /tmp/duplicate-icon-tools/node_modules/@resvg/resvg-js/index.js
import { createRequire } from 'node:module'
import { readFileSync, writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { resolve } from 'node:path'
const require = createRequire(import.meta.url)
const { Resvg } = require(process.argv[2] ? resolve(process.argv[2]) : '@resvg/resvg-js')
const root = fileURLToPath(new URL('../', import.meta.url))
const svg = readFileSync(root + 'scripts/icon.svg', 'utf8')
for (const size of [48, 64, 128, 256, 512]) {
  const png = new Resvg(svg, { fitTo: { mode: 'width', value: size } }).render().asPng()
  writeFileSync(root + `app/ui/images/icon_${size}.png`, png)
  if (size === 64) writeFileSync(root + 'ICON.PNG', png)
  if (size === 256) writeFileSync(root + 'ICON_256.PNG', png)
}
