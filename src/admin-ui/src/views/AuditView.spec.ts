import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { AuditSummary } from '@/api/types'
import { AUDIT, AUDIT_EVENTS, AUDIT_OBJECT_TYPES, PLATFORM } from '@/utils/labels'
import AuditView from './AuditView.vue'

function entry(id: number, overrides: Partial<AuditSummary> = {}): AuditSummary {
  return {
    id, timestamp: '2026-11-02T09:15:00Z', actor: 'o-anna', actorName: 'Anna Andersson', action: 'retrieval-settings', entityType: 'collection',
    entityId: 'c1', entityName: 'Bygglov', entityExists: true, service: null, departmentId: 'd1', classification: 'internal', configVersion: null,
    reason: null, ...overrides,
  }
}

function mountPage() {
  return mount(AuditView, { attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
}

afterEach(() => {
  vi.restoreAllMocks()
  vi.useRealTimers()
  document.body.innerHTML = ''
})

describe('Granskningslogg (admin design §9.1)', () => {
  it('lists entries newest first, filters by period and event, pages by keyset and shows the snapshot as JSON text', async () => {
    vi.useFakeTimers()
    setActivePinia(createPinia())
    const list = vi.spyOn(api.audit, 'list').mockImplementation(async (q) =>
      q.after ? { items: [entry(5, { action: 'publish' })], next: null } : { items: [entry(9), entry(8, { service: 'system:retention', actorName: null, actor: 'system' })], next: 8 },
    )
    vi.spyOn(api.audit, 'get').mockResolvedValue({
      ...entry(9), recordedAt: '2026-11-02T09:15:01Z', actorRoles: 'rag-editor', actorDepartments: '1000', requestId: 'req-1', sourceAddress: null, userAgent: null,
      details: '{"before":{"candidates":100},"after":{"candidates":120}}',
    })

    const wrapper = mountPage()
    await flushPromises()
    expect(wrapper.text()).toContain('Anna Andersson')
    expect(wrapper.text()).toContain(AUDIT_EVENTS['collection.retrieval-settings'])
    expect(wrapper.text()).toContain('system:retention')

    await wrapper.get('[data-test=more]').trigger('click')
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ after: 8 }))
    expect(wrapper.text()).toContain(AUDIT_EVENTS['collection.publish'])

    await wrapper.get('#audit-event').setValue('collection.publish')
    await wrapper.get('#audit-from').setValue('2026-11-01')
    await wrapper.get('#audit-to').setValue('2026-11-01')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()
    const last = list.mock.lastCall![0]
    expect(last.action).toBe('publish')
    expect(last.entityType).toBe('collection') // the event's type of object
    expect(last.after).toBeUndefined()
    // The to-day is inclusive: the API's `to` is exclusive, so it is the next day's start.
    expect(new Date(last.to!).getTime() - new Date(last.from!).getTime()).toBe(24 * 3600 * 1000)

    await wrapper.findAll('tbody tr')[0]!.find('button').trigger('click')
    await flushPromises()
    const snapshot = document.body.querySelector('[data-test=snapshot]')!.textContent!
    expect(snapshot).toContain('"candidates": 120')
    expect(document.body.querySelector('[data-test=detail-code]')!.textContent).toBe('collection.retrieval-settings')
    wrapper.unmount()
  })

  it('shows events in plain words with the code as title, and an unknown event as its code', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.audit, 'list').mockResolvedValue({
      items: [entry(2, { entityType: 'source', action: 'sync' }), entry(1, { entityType: 'widget', action: 'frobnicate' })], next: null,
    })
    const wrapper = mountPage()
    await flushPromises()
    const events = wrapper.findAll('[data-test=event]')
    expect(events.map((e) => e.text())).toEqual(['Fetch started', 'widget.frobnicate'])
    expect(events.map((e) => e.attributes('title'))).toEqual(['source.sync', 'widget.frobnicate'])
    wrapper.unmount()
  })

  it('names the object, links it to its page while it exists, and shows the id when there is no name', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.audit, 'list').mockResolvedValue({
      items: [
        entry(6, { entityType: 'collection', entityId: 'c1', entityName: 'Bygglov', entityExists: true }),
        entry(5, { entityType: 'source', entityId: 's1', entityName: 'Webbsidor', entityExists: false }), // deleted: the snapshot's name
        entry(4, { entityType: 'run', entityId: 'r1', entityName: null, entityExists: true }),
        entry(3, { entityType: 'settings', action: 'update', entityId: 'chat', entityName: null, entityExists: null }),
        entry(2, { entityType: 'pipeline', action: 'publish', entityId: 'p1', entityName: null, entityExists: null }),
        entry(1, { entityType: 'gateway-models', action: 'sync', entityId: null, entityName: null, entityExists: null }),
      ],
      next: null,
    })
    const wrapper = mountPage()
    await flushPromises()
    const rows = wrapper.findAll('tbody tr')
    const links = wrapper.findAllComponents(RouterLinkStub)
    expect(links.map((l) => [l.text(), l.props('to')])).toEqual([
      ['Bygglov', { name: 'collection', params: { id: 'c1' } }],
      ['r1', { name: 'run', params: { id: 'r1' } }],
      [PLATFORM.settingsSections.chat, { name: 'platform-settings', params: { section: 'chat' } }],
    ])
    expect(rows[1]!.get('[data-test=object-name]').text()).toBe('Webbsidor')
    expect(rows[4]!.get('[data-test=object-name]').text()).toBe('p1')
    expect(rows[5]!.get('[data-test=object-name]').text()).toBe('–')
    expect(rows[5]!.find('[data-test=only-this]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('offers the type of object and the event as pickers, and Only this object sets the object filter under More filters', async () => {
    vi.useFakeTimers()
    setActivePinia(createPinia())
    const list = vi.spyOn(api.audit, 'list').mockResolvedValue({
      items: [entry(2, { entityType: 'source', action: 'sync', entityId: 's1', entityName: 'Webbsidor' })], next: null,
    })
    const open = vi.spyOn(api.audit, 'get')
    const wrapper = mountPage()
    await flushPromises()

    const types = wrapper.get('#audit-entity-type').findAll('option')
    expect(types[0]!.text()).toBe(AUDIT.any)
    expect(types.map((o) => o.text())).toContain(AUDIT_OBJECT_TYPES.collection)
    expect(wrapper.get('#audit-event').findAll('option').length).toBe(Object.keys(AUDIT_EVENTS).length + 1)

    // A type narrows the events to its own.
    await wrapper.get('#audit-entity-type').setValue('source')
    const events = wrapper.get('#audit-event').findAll('option').map((o) => o.attributes('value'))
    expect(events[0]).toBe('')
    expect(events.slice(1).every((v) => v!.startsWith('source.'))).toBe(true)
    await wrapper.get('#audit-event').setValue('source.sync')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()
    expect(list.mock.lastCall![0]).toEqual(expect.objectContaining({ entityType: 'source', action: 'sync' }))

    // Another type clears an event of the old one.
    await wrapper.get('#audit-entity-type').setValue('collection')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()
    expect(list.mock.lastCall![0]).toEqual(expect.objectContaining({ entityType: 'collection', action: undefined }))

    const details = wrapper.get('[data-test=more-filters]')
    expect(details.attributes('open')).toBeUndefined()
    const button = wrapper.get('[data-test=only-this]')
    expect(button.attributes('aria-label')).toBe(AUDIT.onlyThisLabel('Webbsidor'))
    await button.trigger('click')
    await vi.advanceTimersByTimeAsync(350)
    await flushPromises()
    expect(open).not.toHaveBeenCalled() // the row's detail stays closed
    expect(list.mock.lastCall![0]).toEqual(expect.objectContaining({ entityType: 'source', entityId: 's1', action: undefined }))
    expect(details.attributes('open')).toBeDefined()
    expect((wrapper.get('#audit-entity-id').element as HTMLInputElement).value).toBe('s1')
    wrapper.unmount()
  })

  it('says so when nothing matches', async () => {
    setActivePinia(createPinia())
    vi.spyOn(api.audit, 'list').mockResolvedValue({ items: [], next: null })
    const wrapper = mountPage()
    await flushPromises()
    expect(wrapper.text()).toContain('No events match')
  })

  it('loads nothing after the page is left while a filter change waits', async () => {
    vi.useFakeTimers()
    setActivePinia(createPinia())
    const list = vi.spyOn(api.audit, 'list').mockResolvedValue({ items: [], next: null })
    const wrapper = mountPage()
    await flushPromises()
    expect(list).toHaveBeenCalledTimes(1)

    await wrapper.get('#audit-from').setValue('2026-11-01')
    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(1_000)
    expect(list).toHaveBeenCalledTimes(1)
  })
})
