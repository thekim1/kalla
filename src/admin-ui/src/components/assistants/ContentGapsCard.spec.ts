import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { ContentGapReport, ContentGapRow } from '@/api/types'
import { CONTENT_GAPS as L, COSTS } from '@/utils/labels'
import { CONTENT_GAP_MESSAGES } from '@/utils/messages'
import ContentGapsCard from './ContentGapsCard.vue'

const ZERO = '00000000-0000-0000-0000-000000000000'

function row(overrides: Partial<ContentGapRow>): ContentGapRow {
  return {
    collectionId: 'c1', documentId: 'd1', noHit: false, collectionName: 'Bygglov', documentTitle: 'Altan och uteplats', deleted: false, notShared: false, canOpen: true, notFound: 1,
    answeredGeneral: 0, ...overrides,
  }
}

function report(documents: ContentGapRow[], over: Partial<ContentGapReport> = {}): ContentGapReport {
  return {
    from: '2026-10-25', to: '2026-11-23', turns: 40, notFound: documents.reduce((t, d) => t + d.notFound, 0),
    answeredGeneral: documents.reduce((t, d) => t + d.answeredGeneral, 0), documents, ...over,
  }
}

async function open(value: ContentGapReport) {
  vi.spyOn(api.gaps, 'assistant').mockResolvedValue(value)
  const wrapper = mount(ContentGapsCard, { props: { assistantId: 'a1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
  await flushPromises()
  return wrapper
}

/** The table's body rows as their cells' text (the narrow-screen labels are in the cells too, so match by containment). */
function rows(wrapper: Awaited<ReturnType<typeof open>>): string[] {
  return wrapper.get('[data-test=content-gaps-documents]').findAll('tbody tr').map((r) => r.text())
}

describe('the Content gaps card (admin design §8.7, B-62)', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    setActivePinia(createPinia())
  })

  it('shows the share line and the nearest documents most first: a link, no hit, a deleted one and one it may not open', async () => {
    const wrapper = await open(report([
      row({ notFound: 4, answeredGeneral: 1 }),
      row({ collectionId: ZERO, documentId: ZERO, noHit: true, collectionName: null, documentTitle: null, canOpen: false, notFound: 3 }),
      row({ documentId: 'd2', documentTitle: null, deleted: true, canOpen: false, notFound: 0, answeredGeneral: 1 }),
      row({ collectionId: 'c2', documentId: 'd3', collectionName: 'Gator', documentTitle: 'Parkering', canOpen: false, notFound: 1 }),
    ]))
    expect(api.gaps.assistant).toHaveBeenCalledWith('a1', expect.objectContaining({ from: expect.any(String), to: expect.any(String) }))
    // 10 of 40 questions: 8 not found, 2 from general knowledge.
    expect(wrapper.get('[data-test=content-gaps-share]').text()).toMatch(/^25\s?% of the questions in this period were not answered from the knowledge \(8 not found, 2 answered from general knowledge\)\.$/)

    const body = rows(wrapper)
    expect(body).toHaveLength(4)
    expect(body[0]).toContain('Altan och uteplats')
    expect(body[0]).toContain('Bygglov')
    expect(body[1]).toContain(L.noHit)
    expect(body[2]).toContain(L.deleted)
    expect(body[3]).toContain('Parkering')
    expect(body[3]).toContain('Gator')

    // Only the document its departments own is linked, to its knowledge base's documents.
    const links = wrapper.findAllComponents(RouterLinkStub)
    expect(links).toHaveLength(1)
    expect(links[0]!.props('to')).toEqual({ name: 'collection', params: { id: 'c1' }, query: { tab: 'documents' } })
    expect(links[0]!.text()).toBe('Altan och uteplats')

    // The CSV of the same period; no Show all for four rows.
    expect(wrapper.get('[data-test=content-gaps-csv]').attributes('href')).toMatch(/^\/api\/assistants\/a1\/gaps\?.*format=csv/)
    expect(wrapper.get('[data-test=content-gaps-csv]').text()).toBe(COSTS.downloadCsv)
    expect(wrapper.find('[data-test=content-gaps-show-all]').exists()).toBe(false)
  })

  it('shows a knowledge base no longer shared with one text, without name, title or link', async () => {
    const wrapper = await open(report([
      row({ collectionId: 'c3', documentId: 'd4', collectionName: null, documentTitle: null, notShared: true, canOpen: false, notFound: 2 }),
    ]))
    const body = rows(wrapper)
    expect(body).toHaveLength(1)
    expect(wrapper.get('[data-test=content-gaps-not-shared]').text()).toBe(L.notShared)
    expect(body[0]).not.toContain(L.deleted)
    expect(wrapper.findAllComponents(RouterLinkStub)).toHaveLength(0)
  })

  it('lists 20 documents until Show all, then all of them', async () => {
    const many = Array.from({ length: 25 }, (_, i) => row({ documentId: `d${i}`, documentTitle: `Sida ${i}`, notFound: 30 - i }))
    const wrapper = await open(report(many, { turns: 1000 }))
    expect(rows(wrapper)).toHaveLength(20)
    const button = wrapper.get('[data-test=content-gaps-show-all]')
    expect(button.text()).toBe(L.showAll(25))
    await button.trigger('click')
    expect(rows(wrapper)).toHaveLength(25)
    expect(wrapper.get('[data-test=content-gaps-show-all]').text()).toBe(L.showFewer)
  })

  it('has an empty state for a period without such a question, and reloads for another period', async () => {
    const wrapper = await open(report([], { turns: 12 }))
    expect(wrapper.get('[data-test=content-gaps-empty]').text()).toBe(CONTENT_GAP_MESSAGES.empty)
    expect(wrapper.find('[data-test=content-gaps-documents]').exists()).toBe(false)

    await wrapper.get('select').setValue('90')
    await flushPromises()
    expect(api.gaps.assistant).toHaveBeenCalledTimes(2)
    const [, first] = vi.mocked(api.gaps.assistant).mock.calls[0]!
    const [, second] = vi.mocked(api.gaps.assistant).mock.calls[1]!
    expect(second!.to).toBe(first!.to)
    expect(second!.from! < first!.from!).toBe(true)
  })
})
