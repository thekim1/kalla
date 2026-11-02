import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import type { ClientSummary } from '@/api/types'
import ExplainAccess from './ExplainAccess.vue'

const clients = [{ id: 'c1', name: 'Open WebUI (pilot)' }] as ClientSummary[]

afterEach(() => vi.restoreAllMocks())

describe('Check access (admin design §4.5, identity §4.9)', () => {
  it('explains a refusal with its step and reason, and an allowed call with level and residency', async () => {
    const explain = vi.spyOn(api.access, 'explain')
      .mockResolvedValueOnce({ allowed: false, step: '4', reason: 'audience_mismatch', status: 404, effectiveLevel: null, maxResidency: null, configVersion: 12 })
      .mockResolvedValueOnce({ allowed: true, step: '', reason: '', status: 200, effectiveLevel: 'internal', maxResidency: 'eu', configVersion: 12 })
    const wrapper = mount(ExplainAccess, { props: { assistantId: 'a1', clients } })

    await wrapper.get('#explain-client').setValue('c1')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.text()).toContain('Enter the id.')
    expect(explain).not.toHaveBeenCalled()

    await wrapper.get('#explain-kind').setValue('entra-user')
    await wrapper.get('#explain-id').setValue('o-anna')
    await wrapper.get('#explain-groups').setValue('rag-bygg, rag-alla')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(explain).toHaveBeenCalledWith({
      clientId: 'c1', assistantId: 'a1', subject: { kind: 'entra-user', id: 'o-anna', departments: [], groups: ['rag-bygg', 'rag-alla'] },
    })
    const refused = wrapper.get('[data-test=explain-result]').text()
    expect(refused).toContain('Refused')
    expect(refused).toContain("not in the assistant's audience")
    expect(refused).toContain('Step 4')

    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('[data-test=explain-result]').text()).toContain('Allowed, at the internal level, with models running at most eu.')
  })
})
