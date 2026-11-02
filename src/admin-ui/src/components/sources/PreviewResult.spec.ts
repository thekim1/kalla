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

describe('Förhandsgranskning (admin design §6.4)', () => {
  it('shows a document without a site profile at its last stage: the personal-data counts and the state the set would get', async () => {
    const output = vi.spyOn(api.previews, 'output').mockResolvedValue({
      stage: 'personal-data', truncated: false,
      value: { counts: { personnummer: 0, email: 2, phone: 1, ner: 'skipped' }, typesFired: ['email'], state: 'held' },
    })
    const wrapper = mount(PreviewResult, { props: { jobId: 'j', result } })
    await flushPromises()

    expect(output).toHaveBeenCalledWith('j', 0, 'personal-data')
    const text = wrapper.text()
    expect(text).not.toContain('Före / Efter')
    expect(text).toContain('Läge i kunskapsbanken: Hålls utanför sökningen')
    expect(text).toContain('E-postadresser2Gör att dokumentet flaggas')
    expect(text).toContain('Telefonnummer1')
    expect(text).not.toContain('Personnummer')
    expect(text).toContain('USD')
  })

  it('says the preview has expired when its outputs are gone (404 after 24 h)', async () => {
    vi.spyOn(api.previews, 'output').mockRejectedValue(new ApiError(404, 'GET', null))
    const wrapper = mount(PreviewResult, { props: { jobId: 'j', result } })
    await flushPromises()
    expect(wrapper.text()).toContain('Förhandsgranskningen finns inte längre (den sparas i 24 timmar). Kör den igen.')
  })
})
