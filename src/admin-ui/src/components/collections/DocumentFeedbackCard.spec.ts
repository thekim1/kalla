import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { DocumentFeedbackReport, DocumentFeedbackRow } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { COSTS, DOCUMENT_FEEDBACK as L } from '@/utils/labels'
import { DOCUMENT_FEEDBACK_MESSAGES } from '@/utils/messages'
import DocumentFeedbackCard from './DocumentFeedbackCard.vue'

function row(overrides: Partial<DocumentFeedbackRow>): DocumentFeedbackRow {
  return { documentId: 'd1', title: 'Altan och uteplats', deleted: false, thumbsDown: 4, thumbsUp: 1, assistants: 2, ...overrides }
}

function report(documents: DocumentFeedbackRow[]): DocumentFeedbackReport {
  return { from: '2026-10-25', to: '2026-11-23', documents }
}

async function open(value: DocumentFeedbackReport, roles = ['rag-department-admin']) {
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
  vi.spyOn(api.documentFeedback, 'collection').mockResolvedValue(value)
  const wrapper = mount(DocumentFeedbackCard, { props: { collectionId: 'c1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
  await flushPromises()
  return wrapper
}

function rows(wrapper: Awaited<ReturnType<typeof open>>): string[] {
  return wrapper.get('[data-test=document-feedback-documents]').findAll('tbody tr').map((r) => r.text())
}

describe('the Feedback on documents card (admin design §8.8, B-66)', () => {
  beforeEach(() => {
    vi.restoreAllMocks()
    setActivePinia(createPinia())
  })

  it('says what a count means and lists the documents most downvoted first, each linked with Show the ratings', async () => {
    const wrapper = await open(report([row({}), row({ documentId: 'd2', title: null, deleted: true, thumbsDown: 1, thumbsUp: 0 })]))
    expect(api.documentFeedback.collection).toHaveBeenCalledWith('c1', expect.objectContaining({ from: expect.any(String), to: expect.any(String) }))
    expect(wrapper.get('[data-test=document-feedback-explanation]').text()).toBe(DOCUMENT_FEEDBACK_MESSAGES.explanation)

    const body = rows(wrapper)
    expect(body).toHaveLength(2)
    expect(body[0]).toContain('Altan och uteplats')
    expect(body[0]).toContain('4')
    expect(body[1]).toContain(L.deleted)

    // The live document opens in its knowledge base; each row opens the Feedback page filtered by its document (§8.1).
    expect(wrapper.get('[data-test=document-feedback-link]').findComponent(RouterLinkStub).props('to'))
      .toEqual({ name: 'collection', params: { id: 'c1' }, query: { tab: 'documents' } })
    const ratings = wrapper.findAll('[data-test=document-feedback-ratings]')
    expect(ratings).toHaveLength(2)
    expect(ratings[0]!.text()).toBe(L.showRatings)
    expect(ratings[0]!.findComponent(RouterLinkStub).props('to')).toEqual({ name: 'feedback', query: { documentId: 'd1' } })
    expect(ratings[1]!.findComponent(RouterLinkStub).props('to')).toEqual({ name: 'feedback', query: { documentId: 'd2' } })

    // No assistant is named: the count of assistants is not a column of the card (it is in the CSV).
    expect(wrapper.text()).not.toContain('assistant')
    expect(wrapper.get('[data-test=document-feedback-csv]').attributes('href')).toMatch(/^\/api\/collections\/c1\/feedback\?.*format=csv/)
    expect(wrapper.get('[data-test=document-feedback-csv]').text()).toBe(COSTS.downloadCsv)
    expect(wrapper.find('[data-test=document-feedback-show-all]').exists()).toBe(false)
  })

  it('offers Show the ratings only to the Feedback page\'s roles', async () => {
    const wrapper = await open(report([row({})]), ['rag-editor'])
    expect(rows(wrapper)).toHaveLength(1)
    expect(wrapper.find('[data-test=document-feedback-ratings]').exists()).toBe(false)
  })

  it('lists 10 documents until Show all, then all of them', async () => {
    const many = Array.from({ length: 14 }, (_, i) => row({ documentId: `d${i}`, title: `Sida ${i}`, thumbsDown: 20 - i }))
    const wrapper = await open(report(many))
    expect(rows(wrapper)).toHaveLength(10)
    const button = wrapper.get('[data-test=document-feedback-show-all]')
    expect(button.text()).toBe(L.showAll(14))
    await button.trigger('click')
    expect(rows(wrapper)).toHaveLength(14)
    expect(wrapper.get('[data-test=document-feedback-show-all]').text()).toBe(L.showFewer)
  })

  it('has an empty state for a period without a rated answer, and reloads for another period', async () => {
    const wrapper = await open(report([]))
    expect(wrapper.get('[data-test=document-feedback-empty]').text()).toBe(DOCUMENT_FEEDBACK_MESSAGES.empty)
    expect(wrapper.find('[data-test=document-feedback-documents]').exists()).toBe(false)

    await wrapper.get('select').setValue('90')
    await flushPromises()
    expect(api.documentFeedback.collection).toHaveBeenCalledTimes(2)
    const [, first] = vi.mocked(api.documentFeedback.collection).mock.calls[0]!
    const [, second] = vi.mocked(api.documentFeedback.collection).mock.calls[1]!
    expect(second!.to).toBe(first!.to)
    expect(second!.from! < first!.from!).toBe(true)
  })
})
