import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { api } from '@/api'
import type { ApiKey, AssistantSummary, CollectionSummary, Department } from '@/api/types'
import { KEY_FORM, PLATFORM } from '@/utils/labels'
import KeyForm from './KeyForm.vue'

const key: ApiKey = {
  id: 'k1', clientId: 'c1', clientName: 'Webb', teamId: 't1', kind: 'sk', name: 'Webb key', description: 'Sök på umea.se', prefix: 'kalla-sk-ab12', status: 'active',
  assistantIds: ['a1'], collectionIds: [], allowedIpRanges: ['10.0.0.0/8'], requestsPerMinute: 60, isEnabled: true, createdAt: '2026-11-13T10:00:00Z', createdBy: null,
  expiresAt: new Date('2027-01-31T23:59:59').toISOString(), revokedAt: null, graceUntil: null, rotatedToKeyId: null, lastUsedAt: null,
}

const departments: Department[] = [
  {
    id: 'd1', name: 'Kommunledningskontoret', costCenterCode: '1000', isActive: true, syncedAt: '2026-11-01T12:00:00Z',
    teams: [{ id: 't1', name: 'Webb', code: null, isActive: true }, { id: 't2', name: 'Gammalt', code: null, isActive: false }],
  },
]
const assistants = [{ id: 'a1', name: 'Bygglovsassistenten' }, { id: 'a2', name: 'Skolassistenten' }, { id: 'a3', name: 'Personal' }] as AssistantSummary[]
const collections = [{ id: 'col1', name: 'Bygglov' }] as CollectionSummary[]

// The drawer reads the pickers' rows and passes them down.
function mountForm(props: { apiKey: ApiKey | null; clientId?: string; audienceIds?: string[] }) {
  return mount(KeyForm, { props: { formId: 'kf', clientId: 'c1', audienceIds: ['a1'], assistants, departments, collections, ...props }, attachTo: document.body })
}

async function submit(wrapper: VueWrapper) {
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

const restrictions = (wrapper: VueWrapper) => wrapper.get('[data-test=key-restrictions]').element as HTMLDetailsElement

describe('Key form (admin design §5.9, §5.9.1)', () => {
  it('asks a new key for its name only, with the restrictions collapsed', async () => {
    const create = vi.spyOn(api.keys, 'create').mockResolvedValue({ key, secret: 'kalla-sk-new' })
    const wrapper = mountForm({ apiKey: null })
    await flushPromises()
    expect(restrictions(wrapper).open).toBe(false)
    // The enabled switch is for an existing key; a new one works at once.
    expect(wrapper.find('[role=switch]').exists()).toBe(false)

    await wrapper.get('#kf-name').setValue('Webb key')
    await submit(wrapper)
    expect(create).toHaveBeenCalledWith({
      clientId: 'c1', teamId: null, name: 'Webb key', description: null, expiresAt: null, scopes: { assistantIds: [], collectionIds: [] },
      allowedIpRanges: [], requestsPerMinute: null, isEnabled: true,
    })
    expect(wrapper.emitted('saved')?.[0]).toEqual([{ key, secret: 'kalla-sk-new' }])
    wrapper.unmount()
  })

  it('creates a key with its restrictions: the team, the app\'s assistants only, the expiry as an ISO instant and the limits', async () => {
    const create = vi.spyOn(api.keys, 'create').mockResolvedValue({ key, secret: 'kalla-sk-new' })
    const wrapper = mountForm({ apiKey: null })
    await flushPromises()
    const teams = [...(wrapper.get('#kf-teamId').element as HTMLSelectElement).options].map((o) => [o.value, o.text])
    expect(teams).toEqual([['', KEY_FORM.noTeam], ['t1', 'Kommunledningskontoret: Webb']])
    expect(wrapper.text()).toContain(KEY_FORM.scopeHint)
    expect(wrapper.text()).toContain(PLATFORM.fieldHints.requestsPerMinute)
    // Only the assistant this app offers; every knowledge base.
    expect(wrapper.get('[data-test=scope-assistants]').findAll('input[type=checkbox]')).toHaveLength(1)
    expect(wrapper.get('[data-test=scope-assistants]').text()).toContain('Bygglovsassistenten')

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
    wrapper.unmount()
  })

  it('opens the restrictions for a refused field inside them', async () => {
    const wrapper = mountForm({ apiKey: null })
    await flushPromises()
    await wrapper.get('#kf-name').setValue('Webb key')
    await wrapper.get('#kf-requestsPerMinute').setValue('sixty')
    expect(restrictions(wrapper).open).toBe(false)
    await submit(wrapper)
    expect(restrictions(wrapper).open).toBe(true)
    expect(wrapper.text()).toContain(PLATFORM.form.invalidNumber)
    wrapper.unmount()
  })

  it('edits a key with its values and the restrictions open, the team fixed, and sends an emptied expiry as null', async () => {
    const update = vi.spyOn(api.keys, 'update').mockResolvedValue(key)
    // The key still names a2, which the app no longer offers: it stays, marked, so it can be unticked.
    const wrapper = mountForm({ apiKey: { ...key, assistantIds: ['a1', 'a2'] } })
    await flushPromises()
    expect(restrictions(wrapper).open).toBe(true)
    expect((wrapper.get('#kf-name').element as HTMLInputElement).value).toBe('Webb key')
    expect((wrapper.get('#kf-expiresAt').element as HTMLInputElement).value).toBe('2027-01-31')
    expect((wrapper.get('#kf-teamId').element as HTMLSelectElement).disabled).toBe(true)
    expect(wrapper.text()).toContain(KEY_FORM.teamFixed)
    const scoped = wrapper.get('[data-test=scope-assistants]')
    expect(scoped.findAll('input[type=checkbox]')).toHaveLength(2)
    expect(scoped.text()).toContain(KEY_FORM.notOffered)
    expect(scoped.text()).not.toContain('Personal')

    await wrapper.get('#kf-expiresAt').setValue('')
    await wrapper.get('#kf-requestsPerMinute').setValue('')
    await submit(wrapper)
    expect(update).toHaveBeenCalledWith('k1', {
      name: 'Webb key', description: 'Sök på umea.se', expiresAt: null, scopes: { assistantIds: ['a1', 'a2'], collectionIds: [] }, allowedIpRanges: ['10.0.0.0/8'],
      requestsPerMinute: null, isEnabled: true,
    })
    wrapper.unmount()
  })
})
