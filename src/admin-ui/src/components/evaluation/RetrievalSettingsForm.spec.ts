import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Collection } from '@/api/types'
import RetrievalSettingsForm from './RetrievalSettingsForm.vue'

const collection: Collection = {
  id: 'c1', slug: 'bygglov', name: 'Bygglov', departmentId: 'd1', classification: 'internal', publishedAt: null, ownerTeamId: null, embeddingProfileId: 'p',
  retrievalSettings: { candidates: 120, recency: { halfLifeDays: 730, weight: 0.15, kinds: ['pdf'] } },
  usedBy: [], updatedAt: '2026-11-01T10:00:00Z', updatedBy: null, version: '7',
}

afterEach(() => vi.restoreAllMocks())

describe('Search settings (admin design §7.1, V16)', () => {
  it('shows the saved values, checks the ranges and saves only the fields set, with the version read', async () => {
    setActivePinia(createPinia())
    const save = vi.spyOn(api.collections, 'retrievalSettings').mockResolvedValue({ ...collection, version: '8' })
    const wrapper = mount(RetrievalSettingsForm, { props: { collection, canEdit: true } })

    expect((wrapper.get('#rs-candidates').element as HTMLInputElement).value).toBe('120')
    expect((wrapper.get('#rs-halfLifeDays').element as HTMLInputElement).value).toBe('730')
    expect((wrapper.get('#rs-weight').element as HTMLInputElement).value).toBe('0,15')

    await wrapper.get('#rs-coverageMaxDistance').setValue('1,5')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.text()).toContain('Enter a number from 0,05 to 1.')
    expect(save).not.toHaveBeenCalled()

    await wrapper.get('#rs-coverageMaxDistance').setValue('0,42')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save).toHaveBeenCalledWith('c1', { candidates: 120, coverageMaxDistance: 0.42, recency: { halfLifeDays: 730, weight: 0.15, kinds: ['pdf'] } }, '7')
    expect(wrapper.emitted('saved')).toHaveLength(1)
  })

  it('saves recency switched off as null, fills in the proposal, and tries values without saving', async () => {
    setActivePinia(createPinia())
    const save = vi.spyOn(api.collections, 'retrievalSettings').mockResolvedValue(collection)
    const run = vi.spyOn(api.evaluation, 'run').mockResolvedValue({ jobId: 'j', alreadyQueued: false })
    const wrapper = mount(RetrievalSettingsForm, { props: { collection, canEdit: true, proposal: { coverageMaxDistance: 0.38, coverageMinKeywordScore: 2.5 } } })

    expect(wrapper.find('[data-test=proposal-filled]').exists()).toBe(true)
    expect((wrapper.get('#rs-coverageMinKeywordScore').element as HTMLInputElement).value).toBe('2,5')

    await wrapper.get('[data-test=try-retrieval]').trigger('click')
    await flushPromises()
    expect(run).toHaveBeenCalledWith('c1', {
      candidateSettings: { candidates: 120, coverageMaxDistance: 0.38, coverageMinKeywordScore: 2.5, recency: { halfLifeDays: 730, weight: 0.15, kinds: ['pdf'] } },
    })
    expect(save).not.toHaveBeenCalled()

    await wrapper.get('[data-test=recency-on] input').setValue(false)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(save).toHaveBeenCalledWith('c1', { candidates: 120, coverageMaxDistance: 0.38, coverageMinKeywordScore: 2.5, recency: null }, '7')
  })

  it('is read-only without Edit', () => {
    setActivePinia(createPinia())
    const wrapper = mount(RetrievalSettingsForm, { props: { collection, canEdit: false } })
    expect(wrapper.find('[data-test=save-retrieval]').exists()).toBe(false)
    expect(wrapper.get('#rs-candidates').attributes('disabled')).toBeDefined()
  })
})
