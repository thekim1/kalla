import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { StagePreviewResult } from '@/api/types'
import PreviewResult from './PreviewResult.vue'

const result: StagePreviewResult = {
  expiresAt: '2026-11-03T10:00:00Z',
  documents: [
    {
      documentId: 'doc1',
      stages: [
        { stage: 'parse', state: 'succeeded', output: 'previews/j/0/parse.json' },
        { stage: 'personal-data', state: 'succeeded', output: 'previews/j/0/personal-data.json', estimatedCostUsd: 0.0012 },
      ],
    },
  ],
}

describe('Preview (admin design §6.4)', () => {
  it('shows a document without a site profile at its last stage: the personal-data counts and the state the set would get', async () => {
    const output = vi.spyOn(api.previews, 'output').mockResolvedValue({
      stage: 'personal-data', truncated: false,
      value: { counts: { personnummer: 0, email: 2, phone: 1, ner: 'skipped' }, typesFired: ['email'], state: 'held' },
    })
    const wrapper = mount(PreviewResult, { props: { jobId: 'j', result } })
    await flushPromises()

    expect(output).toHaveBeenCalledWith('j', 0, 'personal-data')
    const text = wrapper.text()
    expect(text).not.toContain('Before / After')
    expect(text).toContain('State in the knowledge base: Kept out of the search')
    expect(text).toContain('Email addresses2Makes the document flagged')
    expect(text).toContain('Phone numbers1')
    expect(text).not.toContain('Personal identity numbers')
    expect(text).toContain('US$')
  })

  it('says the preview has expired when its outputs are gone (404 after 24 h)', async () => {
    vi.spyOn(api.previews, 'output').mockRejectedValue(new ApiError(404, 'GET', null))
    const wrapper = mount(PreviewResult, { props: { jobId: 'j', result } })
    await flushPromises()
    expect(wrapper.text()).toContain('The preview no longer exists (it is kept for 24 hours). Run it again.')
  })
})
