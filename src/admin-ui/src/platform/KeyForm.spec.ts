import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { api } from '@/api'
import type { ApiKey, AssistantSummary, CollectionSummary } from '@/api/types'
import { KEY_FORM, PLATFORM } from '@/utils/labels'
import KeyForm from './KeyForm.vue'

const key: ApiKey = {
  id: 'k1', clientId: 'c1', clientName: 'Webb', teamId: 't1', kind: 'sk', name: 'Webb key', description: 'Sök på umea.se', prefix: 'ume-rag-sk-ab12', status: 'active',
  assistantIds: ['a1'], collectionIds: [], allowedIpRanges: ['10.0.0.0/8'], requestsPerMinute: 60, isEnabled: true, createdAt: '2026-11-13T10:00:00Z', createdBy: null,
  expiresAt: new Date('2027-01-31T23:59:59').toISOString(), revokedAt: null, graceUntil: null, rotatedToKeyId: null,
}

function mountForm(props: { apiKey: ApiKey | null; clientId: string | null }) {
  return mount(KeyForm, { props: { formId: 'kf', ...props }, attachTo: document.body })
}

async function submit(wrapper: VueWrapper) {
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

describe('Key form (admin design §5.9)', () => {
  beforeEach(() => {
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([
      {
        id: 'd1', name: 'Kommunledningskontoret', costCenterCode: '1000', isActive: true, syncedAt: '2026-11-01T12:00:00Z',
        teams: [{ id: 't1', name: 'Webb', code: null, isActive: true }, { id: 't2', name: 'Gammalt', code: null, isActive: false }],
      },
    ])
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ items: [{ id: 'a1', name: 'Bygglovsassistenten' }, { id: 'a2', name: 'Skolassistenten' }] as AssistantSummary[], total: 2 })
    vi.spyOn(api.collections, 'list').mockResolvedValue({ items: [{ id: 'col1', name: 'Bygglov' }] as CollectionSummary[], total: 1 })
  })

  it('creates a key for its client with the team, scopes as one object, the expiry as an ISO instant and the limits', async () => {
    const create = vi.spyOn(api.keys, 'create').mockResolvedValue({ key, secret: 'ume-rag-sk-new' })
    const wrapper = mountForm({ apiKey: null, clientId: 'c1' })
    await flushPromises()
    expect(wrapper.find('#kf-clientId').exists()).toBe(false)
    const teams = [...(wrapper.get('#kf-teamId').element as HTMLSelectElement).options].map((o) => [o.value, o.text])
    expect(teams).toEqual([['', KEY_FORM.noTeam], ['t1', 'Kommunledningskontoret: Webb']])
    expect(wrapper.text()).toContain(KEY_FORM.scopeHint)
    expect(wrapper.text()).toContain(PLATFORM.fieldHints.requestsPerMinute)

    await wrapper.get('#kf-name').setValue('Webb key')
    await wrapper.get('#kf-teamId').setValue('t1')
    await wrapper.get('[data-test=scope-assistants] input[type=checkbox]').setValue(true)
    await wrapper.get('[data-test=scope-collections] input[type=checkbox]').setValue(true)
    await wrapper.get('#kf-expiresAt').setValue('2026-12-31')
    await wrapper.get('#kf-allowedIpRanges').setValue('10.0.0.0/8\n\n192.168.1.0/24\n')
    await wrapper.get('#kf-requestsPerMinute').setValue('60')
    await submit(wrapper)

    expect(create).toHaveBeenCalledWith({
      clientId: 'c1', teamId: 't1', name: 'Webb key', description: null, expiresAt: new Date('2026-12-31T23:59:59').toISOString(),
      scopes: { assistantIds: ['a1'], collectionIds: ['col1'] }, allowedIpRanges: ['10.0.0.0/8', '192.168.1.0/24'], requestsPerMinute: 60, isEnabled: true,
    })
    expect(wrapper.emitted('saved')?.[0]).toEqual([{ key, secret: 'ume-rag-sk-new' }])
    wrapper.unmount()
  })

  it('edits a key with its values, the team fixed, and sends an emptied expiry as null', async () => {
    const update = vi.spyOn(api.keys, 'update').mockResolvedValue(key)
    const wrapper = mountForm({ apiKey: key, clientId: 'c1' })
    await flushPromises()
    expect((wrapper.get('#kf-name').element as HTMLInputElement).value).toBe('Webb key')
    expect((wrapper.get('#kf-expiresAt').element as HTMLInputElement).value).toBe('2027-01-31')
    expect((wrapper.get('#kf-teamId').element as HTMLSelectElement).disabled).toBe(true)
    expect(wrapper.text()).toContain(KEY_FORM.teamFixed)

    await wrapper.get('#kf-expiresAt').setValue('')
    await wrapper.get('#kf-requestsPerMinute').setValue('')
    await submit(wrapper)
    expect(update).toHaveBeenCalledWith('k1', {
      name: 'Webb key', description: 'Sök på umea.se', expiresAt: null, scopes: { assistantIds: ['a1'], collectionIds: [] }, allowedIpRanges: ['10.0.0.0/8'],
      requestsPerMinute: null, isEnabled: true,
    })
    wrapper.unmount()
  })

  it('on the Keys page, picks the client first and requires it', async () => {
    vi.spyOn(api.clients, 'list').mockResolvedValue([{ id: 'c1', name: 'Webb', kind: 'integration', classificationCeiling: 'public', isEnabled: true, activeKeys: 0 }])
    const create = vi.spyOn(api.keys, 'create').mockResolvedValue({ key, secret: 's' })
    const wrapper = mountForm({ apiKey: null, clientId: null })
    await flushPromises()
    await wrapper.get('#kf-name').setValue('Webb key')
    await submit(wrapper)
    expect(wrapper.text()).toContain(KEY_FORM.clientRequired)
    expect(create).not.toHaveBeenCalled()

    await wrapper.get('#kf-clientId').setValue('c1')
    await submit(wrapper)
    expect(create.mock.calls[0]![0]).toMatchObject({ clientId: 'c1', teamId: null, scopes: { assistantIds: [], collectionIds: [] }, expiresAt: null })
    wrapper.unmount()
  })
})
