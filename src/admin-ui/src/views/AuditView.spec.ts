import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { AuditSummary } from '@/api/types'
import AuditView from './AuditView.vue'

function entry(id: number, overrides: Partial<AuditSummary> = {}): AuditSummary {
  return {
    id, timestamp: '2026-11-02T09:15:00Z', actor: 'o-anna', actorName: 'Anna Andersson', action: 'retrieval-settings', entityType: 'collection',
    entityId: 'c1', service: null, departmentId: 'd1', classification: 'internal', configVersion: null, reason: null, ...overrides,
  }
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.useRealTimers()
  document.body.innerHTML = ''
})

describe('Granskningslogg (admin design §9.1)', () => {
  it('lists entries newest first, filters by period and action, pages by keyset and shows the snapshot as JSON text', async () => {
    vi.useFakeTimers()
    setActivePinia(createPinia())
    const list = vi.spyOn(api.audit, 'list').mockImplementation(async (q) =>
      q.after ? { items: [entry(5, { action: 'publish' })], next: null } : { items: [entry(9), entry(8, { service: 'system:retention', actorName: null, actor: 'system' })], next: 8 },
    )
    vi.spyOn(api.audit, 'get').mockResolvedValue({
      ...entry(9), recordedAt: '2026-11-02T09:15:01Z', actorRoles: 'rag-editor', actorDepartments: '1000', requestId: 'req-1', sourceAddress: null, userAgent: null,
      details: '{"before":{"candidates":100},"after":{"candidates":120}}',
    })

    const wrapper = mount(AuditView, { attachTo: document.body })
    await flushPromises()
    expect(wrapper.text()).toContain('Anna Andersson')
    expect(wrapper.text()).toContain('collection.retrieval-settings')
    expect(wrapper.text()).toContain('system:retention')

    await wrapper.get('[data-test=more]').trigger('click')
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ after: 8 }))
    expect(wrapper.text()).toContain('collection.publish')

    await wrapper.get('#audit-action').setValue('publish')
    await wrapper.get('#audit-from').setValue('2026-11-01')
    await wrapper.get('#audit-to').setValue('2026-11-01')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()
    const last = list.mock.lastCall![0]
    expect(last.action).toBe('publish')
    expect(last.after).toBeUndefined()
    // The to-day is inclusive: the API's `to` is exclusive, so it is the next day's start.
    expect(new Date(last.to!).getTime() - new Date(last.from!).getTime()).toBe(24 * 3600 * 1000)

    await wrapper.findAll('tbody tr')[0]!.find('button').trigger('click')
    await flushPromises()
    const snapshot = document.body.querySelector('[data-test=snapshot]')!.textContent!
    expect(snapshot).toContain('"candidates": 120')
    wrapper.unmount()
  })

  it('says so when nothing matches', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.audit, 'list').mockResolvedValue({ items: [], next: null })
    const wrapper = mount(AuditView)
    await flushPromises()
    expect(wrapper.text()).toContain('No events match')
  })
})
