import { describe, expect, it } from 'vitest'
import type { MapRow } from '@/api/types'
import { MAP } from '@/utils/labels'
import { connected, drawingOf, feeds } from './mapDrawing'

// The design's example (admin design §5.5.6): Stadsbyggnad's sources, knowledge bases, assistants and channels.
function kb(id: string, name: string, sources: [string, string][], shared?: string): MapRow['collection'] {
  return {
    id, name, departmentId: shared ? 'teknik' : 'sbk', departmentName: shared ?? 'Stadsbyggnad', shared: !!shared, sharedWith: shared ? 'municipality' : 'department',
    classification: 'internal', published: true, contactName: null, contactEmail: null, sources: sources.map(([sid, sname]) => ({ id: sid, name: sname, type: 'crawl' })),
  }
}
const bygglov = kb('c1', 'Bygglov', [['s1', 'umea.se/bygga'], ['s2', 'Blanketter']])
const detaljplaner = kb('c2', 'Detaljplaner', [['s3', 'umea.se/plan']])
const avfall = kb('c3', 'Avfall', [], 'Teknik')
const permits = { id: 'a1', name: 'Bygglovsassistenten', state: 'active', publishedVersion: 3, draftOnly: false, topics: [], contactName: null, contactEmail: null }
const service = { ...permits, id: 'a2', name: 'Kundtjänst bygg', publishedVersion: null }
const owui = { clientId: 'k1', name: 'Open WebUI (pilot)', kind: 'open-webui' }
const rows: MapRow[] = [
  { collection: bygglov, assistant: permits, audience: owui },
  { collection: bygglov, assistant: { ...service, draftOnly: true }, audience: null },
  { collection: detaljplaner, assistant: permits, audience: owui },
  { collection: avfall, assistant: { ...service, draftOnly: true }, audience: null },
]

describe('The Map as a drawing (admin design §5.5.6, B-67)', () => {
  const d = drawingOf(rows)
  const names = (stage: keyof typeof d.columns) => d.columns[stage].map((n) => n.name)

  it('has one column per stage, each item once, in the rows’ order', () => {
    expect(names('source')).toEqual(['umea.se/bygga', 'Blanketter', 'umea.se/plan'])
    expect(names('collection')).toEqual(['Bygglov', 'Detaljplaner', 'Avfall'])
    expect(names('assistant')).toEqual(['Bygglovsassistenten', 'Kundtjänst bygg'])
    expect(names('audience')).toEqual(['Open WebUI (pilot)'])
    expect(d.columns.collection.map((n) => n.detail)).toEqual(['Internal', 'Internal', MAP.sharedIn('Teknik')])
    expect(d.columns.assistant.map((n) => n.detail)).toEqual([MAP.version(3), MAP.notPublished])
    expect(d.columns.collection[0]!.to).toEqual({ name: 'collection', params: { id: 'c1' } })
    expect(d.columns.source[0]!.to).toEqual({ name: 'source', params: { id: 's1' } })
    expect(d.columns.audience[0]!.to).toBeNull()
  })

  it('draws each relation once, a link only the draft has as a draft line', () => {
    expect(d.edges).toHaveLength(8)
    expect(d.edges.filter((e) => e.draft).map((e) => `${e.from}>${e.to}`)).toEqual(['collection:c1>assistant:a2', 'collection:c3>assistant:a2'])
    expect(d.edges.filter((e) => e.to === 'audience:k1')).toHaveLength(1)
  })

  it('carries the same relations as text', () => {
    expect(feeds(d, 'source:s2')).toEqual(['Bygglov'])
    expect(feeds(d, 'collection:c1')).toEqual(['Bygglovsassistenten', MAP.drawing.draftLink('Kundtjänst bygg')])
    expect(feeds(d, 'assistant:a1')).toEqual(['Open WebUI (pilot)'])
    expect(feeds(d, 'audience:k1')).toEqual([])
  })

  it('highlights what an item feeds and what feeds it, not its siblings', () => {
    expect([...connected(d, 'collection:c2')].sort()).toEqual(['assistant:a1', 'audience:k1', 'collection:c2', 'source:s3'])
    // A source lights its knowledge base and onwards, not the other source of the same knowledge base.
    expect(connected(d, 'source:s1').has('source:s2')).toBe(false)
    expect([...connected(d, 'assistant:a2')].sort()).toEqual(['assistant:a2', 'collection:c1', 'collection:c3', 'source:s1', 'source:s2'])
  })

  it('keeps a link published when one row has it published and another only in the draft', () => {
    const both = drawingOf([rows[0]!, { ...rows[0]!, assistant: { ...permits, draftOnly: true }, audience: null }])
    expect(both.edges.find((e) => e.to === 'assistant:a1')!.draft).toBe(false)
  })
})
