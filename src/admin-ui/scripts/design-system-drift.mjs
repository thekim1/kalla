// Design-system drift report (repo-and-deployment §2.3): compares the files listed in design-system.lock with the copies
// here and, when a gateway checkout is given, with the gateway's current files. It only warns (exit 0), so the two admin
// UIs stay a family without coupled releases.
//
//   node scripts/design-system-drift.mjs [path/to/llmgateway]
import { createHash } from 'node:crypto'
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import process from 'node:process'

const lock = JSON.parse(readFileSync(new URL('../design-system.lock', import.meta.url), 'utf8'))
const root = new URL('..', import.meta.url).pathname
const gateway = process.argv[2] ? join(process.argv[2], lock.sourceDirectory) : null
const sha256 = (path) => createHash('sha256').update(readFileSync(path)).digest('hex')
const warnings = []

for (const file of lock.files) {
  const local = join(root, file.path)
  if (!existsSync(local)) warnings.push(`${file.path}: listed in the lock but missing here`)
  else if (file.copy === 'unchanged' && sha256(local) !== file.sha256) warnings.push(`${file.path}: changed here although copied unchanged; mark it adapted or restore it`)

  if (gateway) {
    const upstream = join(gateway, file.path)
    if (!existsSync(upstream)) warnings.push(`${file.path}: removed in the gateway since ${lock.commit.slice(0, 8)}`)
    else if (sha256(upstream) !== file.sha256) warnings.push(`${file.path}: changed in the gateway since ${lock.commit.slice(0, 8)}; review and copy (${file.copy})`)
  }
}

for (const warning of warnings) console.log(`::warning title=Design-system drift::${warning}`)
console.log(`Design system: ${lock.files.length} files, ${warnings.length} drift warnings${gateway ? '' : ' (no gateway checkout given: local copies only)'}.`)
