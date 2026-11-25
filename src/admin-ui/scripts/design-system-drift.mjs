// Design-system drift report (repo-and-deployment §2.3): compares the files listed in design-system.lock with the copies
// here and, when a gateway checkout is given, with the gateway's current files. It only warns (exit 0), so the two admin
// UIs stay a family without coupled releases.
//
//   node scripts/design-system-drift.mjs [path/to/llmgateway]
import { Buffer } from 'node:buffer'
import { createHash } from 'node:crypto'
import { existsSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import process from 'node:process'

const lock = JSON.parse(readFileSync(new URL('../design-system.lock', import.meta.url), 'utf8'))
const root = new URL('..', import.meta.url).pathname
const gateway = process.argv[2] ? join(process.argv[2], lock.sourceDirectory) : null
// Line endings don't count: a Windows checkout of the gateway has CRLF, which made every file look changed upstream.
const hash = (data) => createHash('sha256').update(data).digest('hex')
/** The file matches the locked hash as it is or with CRLF line endings as LF (a lock entry may have been taken either way). */
const matches = (path, sha) => {
  const data = readFileSync(path)
  return hash(data) === sha || hash(Buffer.from(data.toString('binary').replaceAll('\r\n', '\n'), 'binary')) === sha
}
const warnings = []

for (const file of lock.files) {
  const local = join(root, file.path)
  if (!existsSync(local)) warnings.push(`${file.path}: listed in the lock but missing here`)
  else if (file.copy === 'unchanged' && !matches(local, file.sha256)) warnings.push(`${file.path}: changed here although copied unchanged; mark it adapted or restore it`)

  if (gateway) {
    const upstream = join(gateway, file.path)
    if (!existsSync(upstream)) warnings.push(`${file.path}: removed in the gateway since ${lock.commit.slice(0, 8)}`)
    else if (!matches(upstream, file.sha256)) warnings.push(`${file.path}: changed in the gateway since ${lock.commit.slice(0, 8)}; review and copy (${file.copy})`)
  }
}

for (const warning of warnings) console.log(`::warning title=Design-system drift::${warning}`)
console.log(`Design system: ${lock.files.length} files, ${warnings.length} drift warnings${gateway ? '' : ' (no gateway checkout given: local copies only)'}.`)
