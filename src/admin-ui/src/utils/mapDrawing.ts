import type { RouteLocationRaw } from 'vue-router'
import type { MapRow, SourceType } from '@/api/types'
import { LEVEL_LABEL, MAP, SOURCE_TYPE_LABEL } from '@/utils/labels'

/** The drawing's columns, left to right (admin design §5.5.6). */
export const STAGES = ['source', 'collection', 'assistant', 'audience'] as const
export type Stage = (typeof STAGES)[number]

export interface MapNode {
  /** `stage:id`, unique in one drawing. */
  key: string
  stage: Stage
  name: string
  /** The line under the name: the source's type, the knowledge base's level or owner, the assistant's version. */
  detail: string | null
  to: RouteLocationRaw | null
}

/** One line of the drawing; `draft` when the link is only in the assistant's draft. */
export interface MapEdge { from: string; to: string; draft: boolean }

export interface MapDrawing {
  columns: Record<Stage, MapNode[]>
  edges: MapEdge[]
}

function assistantDetail(a: NonNullable<MapRow['assistant']>): string {
  return a.publishedVersion ? MAP.version(a.publishedVersion) : MAP.notPublished
}

/**
 * The drawing of one department's map rows: each source, knowledge base, assistant and channel once, in the order the
 * rows give (the API sorts them, own knowledge bases first), sources in their knowledge base's order; and each relation
 * once. A link only the assistant's draft has is a draft line.
 */
export function drawingOf(rows: MapRow[]): MapDrawing {
  const columns: Record<Stage, MapNode[]> = { source: [], collection: [], assistant: [], audience: [] }
  const seen = new Set<string>()
  const edges = new Map<string, MapEdge>()
  const add = (node: MapNode) => {
    if (seen.has(node.key)) return
    seen.add(node.key)
    columns[node.stage].push(node)
  }
  const link = (from: string, to: string, draft = false) => {
    const id = `${from}>${to}`
    const was = edges.get(id)
    // Published wins: a link that one row has published is not a draft line.
    edges.set(id, { from, to, draft: was ? was.draft && draft : draft })
  }
  for (const row of rows) {
    const c = row.collection
    const collection = `collection:${c.id}`
    for (const s of c.sources) {
      add({ key: `source:${s.id}`, stage: 'source', name: s.name, detail: SOURCE_TYPE_LABEL[s.type as SourceType] ?? s.type, to: { name: 'source', params: { id: s.id } } })
      link(`source:${s.id}`, collection)
    }
    add({
      key: collection, stage: 'collection', name: c.name, detail: c.shared ? MAP.sharedIn(c.departmentName) : LEVEL_LABEL[c.classification],
      to: { name: 'collection', params: { id: c.id } },
    })
    const a = row.assistant
    if (!a) continue
    const assistant = `assistant:${a.id}`
    add({ key: assistant, stage: 'assistant', name: a.name, detail: assistantDetail(a), to: { name: 'assistant', params: { id: a.id } } })
    link(collection, assistant, a.draftOnly)
    if (!row.audience) continue
    const audience = `audience:${row.audience.clientId}`
    add({ key: audience, stage: 'audience', name: row.audience.name, detail: null, to: null })
    link(assistant, audience)
  }
  return { columns, edges: [...edges.values()] }
}

/** What a node feeds (its lines to the right), as names; a draft link says so. */
export function feeds(drawing: MapDrawing, key: string): string[] {
  const names = new Map(STAGES.flatMap((s) => drawing.columns[s]).map((n) => [n.key, n.name]))
  return drawing.edges.filter((e) => e.from === key).map((e) => (e.draft ? MAP.drawing.draftLink(names.get(e.to)!) : names.get(e.to)!))
}

/** The node and everything upstream and downstream of it: what a selection highlights. */
export function connected(drawing: MapDrawing, key: string): Set<string> {
  const out = new Set([key])
  // Downstream and upstream separately, so a sibling (another source of the same knowledge base) isn't lit.
  const walk = (k: string, down: boolean): void => {
    for (const e of drawing.edges) {
      const [here, next] = down ? [e.from, e.to] : [e.to, e.from]
      if (here !== k || out.has(next)) continue
      out.add(next)
      walk(next, down)
    }
  }
  walk(key, true)
  walk(key, false)
  return out
}
