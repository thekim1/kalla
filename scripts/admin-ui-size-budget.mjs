// Admin UI size budget (repo-and-deployment §2.3, admin design §10.2): gzip sizes of the built JavaScript, failing
// above budget. Run by the admin-ui job in .github/workflows/verify.yml after `npm run build`. Node standard library
// only.
//
//   node scripts/admin-ui-size-budget.mjs <dist> [--landing <chunk>] [--initial-kb 200] [--lazy-kb 100]
//
// Initial JS is what the first page load fetches: the entry scripts and modulepreload links in <dist>/index.html, the
// chunks they import statically, and, with --landing, the route chunk of the landing page (admin design §10.2
// budgets "shell + Översikt"; the chunk is named after its view, e.g. OverviewView-<hash>.js). A lazy route is a chunk
// loaded by a dynamic import(); its size is that chunk plus the chunks it imports statically that are not already
// initial, i.e. what navigating to the route downloads. The landing route counts as initial only. Sizes are gzip at
// zlib's default level, in kB of 1,000 bytes as in Vite's build report.
import { existsSync, readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import process from 'node:process'
import { parseArgs } from 'node:util'
import { gzipSync } from 'node:zlib'

const { values, positionals } = parseArgs({
  allowPositionals: true,
  options: {
    landing: { type: 'string' },
    'initial-kb': { type: 'string', default: '200' },
    'lazy-kb': { type: 'string', default: '100' },
  },
})
const fail = (message) => {
  console.log(`::error title=Size budget::${message}`)
  process.exit(1)
}
if (positionals.length !== 1) {
  fail('usage: admin-ui-size-budget.mjs <dist> [--landing <chunk>] [--initial-kb N] [--lazy-kb N]')
}
const initialBudget = Number(values['initial-kb']) * 1000
const lazyBudget = Number(values['lazy-kb']) * 1000
if (!(initialBudget > 0) || !(lazyBudget > 0)) fail('--initial-kb and --lazy-kb must be positive numbers')

const dist = positionals[0]
const assets = join(dist, 'assets')
if (!existsSync(join(dist, 'index.html')) || !existsSync(assets)) {
  fail(`${dist} has no index.html or assets/; run the build first`)
}

// Every chunk in assets/, with its gzip size and the chunks it imports statically and dynamically. Vite emits
// imports between chunks as relative specifiers ("./name-hash.js"); anything that is not a chunk here is ignored.
const chunks = new Map()
for (const name of readdirSync(assets).filter((file) => file.endsWith('.js')).sort()) {
  const source = readFileSync(join(assets, name))
  chunks.set(name, { source: source.toString('utf8'), gzip: gzipSync(source).length })
}
const specifiers = (source, pattern) =>
  [...source.matchAll(pattern)].map((match) => match[1]).filter((name) => chunks.has(name))
for (const chunk of chunks.values()) {
  chunk.imports = specifiers(chunk.source, /(?:\bfrom|\bimport)\s*["'`]\.\/([^"'`]+\.js)["'`]/g)
  chunk.dynamic = specifiers(chunk.source, /\bimport\(\s*["'`]\.\/([^"'`]+\.js)["'`]\s*\)/g)
}
const closure = (roots) => {
  const seen = new Set()
  const visit = (name) => {
    if (seen.has(name)) return
    seen.add(name)
    chunks.get(name).imports.forEach(visit)
  }
  roots.forEach(visit)
  return seen
}
const total = (names) => [...names].reduce((sum, name) => sum + chunks.get(name).gzip, 0)

// The page's own scripts and preloads: <script type="module" src="/assets/x.js"> and <link rel="modulepreload">.
const html = readFileSync(join(dist, 'index.html'), 'utf8')
const tags = [...html.matchAll(/<(?:script|link)\b[^>]*>/g)].map((match) => match[0])
const pageChunks = tags
  .filter((tag) => /^<script\b/.test(tag) || /\brel=["']?modulepreload\b/.test(tag))
  .map((tag) => /\b(?:src|href)=["']?[^"' >]*\/assets\/([^"' >]+\.js)/.exec(tag)?.[1])
  .filter(Boolean)
const missing = pageChunks.filter((name) => !chunks.has(name))
if (pageChunks.length === 0) fail('index.html loads no script from assets/')
if (missing.length > 0) fail(`index.html loads chunks that are not in assets/: ${missing.join(', ')}`)

const routes = [...new Set([...chunks.values()].flatMap((chunk) => chunk.dynamic))].sort()
let landing = []
if (values.landing) {
  landing = routes.filter((name) => name.startsWith(`${values.landing}-`))
  if (landing.length !== 1) {
    fail(`--landing ${values.landing} matches ${landing.length} lazy route chunks (${routes.join(', ') || 'none'})`)
  }
}
const shell = closure(pageChunks)
const initial = closure([...pageChunks, ...landing])

const kb = (bytes) => `${(bytes / 1000).toFixed(2)} kB`
const role = (name) => {
  if (pageChunks.includes(name)) return 'initial (index.html)'
  if (landing.includes(name)) return 'initial (landing route)'
  if (shell.has(name)) return 'initial (imported)'
  if (initial.has(name)) return 'initial (imported by landing route)'
  if (routes.includes(name)) return 'lazy route'
  if (routes.some((route) => closure([route]).has(name))) return 'shared by lazy routes'
  return 'not referenced'
}
const rows = [...chunks.keys()].map((name) => [name, role(name), kb(chunks.get(name).gzip)])
const results = [['Initial JS', kb(total(initial)), initialBudget, total(initial)]]
for (const route of routes.filter((name) => !landing.includes(name))) {
  const size = total([...closure([route])].filter((name) => !initial.has(name)))
  results.push([`Route ${route}`, kb(size), lazyBudget, size])
}

const table = (header, body) => {
  const widths = header.map((_, column) => Math.max(...[header, ...body].map((row) => String(row[column]).length)))
  for (const row of [header, ...body]) {
    console.log(row.map((cell, column) => String(cell).padEnd(widths[column])).join('  ').trimEnd())
  }
}
table(['Chunk', 'Loaded as', 'gzip'], rows)
console.log('')
table(
  ['Budget', 'gzip', 'Limit', 'Result'],
  results.map(([what, size, limit, bytes]) => [what, size, kb(limit), bytes <= limit ? 'ok' : 'OVER']),
)

const over = results.filter(([, , limit, bytes]) => bytes > limit)
for (const [what, size, limit] of over) {
  console.log(`::error title=Size budget::${what} is ${size} gzip, above the budget of ${kb(limit)}`)
}
process.exit(over.length > 0 ? 1 : 0)
