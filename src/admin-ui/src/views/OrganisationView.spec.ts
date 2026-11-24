import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { bffUser, department } from '@/test/fixtures'
import { PLATFORM } from '@/utils/labels'
import OrganisationView from './OrganisationView.vue'

describe('Organisation: synced departments (P0 checklist #11)', () => {
  it('lists the synced departments with their teams', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser()
    auth.status = 'authenticated'
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([
      department({ name: 'Kommunledningskontoret', costCenterCode: '1000', teams: [{ id: 't', name: 'Digitalisering & AI', code: 'digitalisering-ai', isActive: true }] }),
      department({ name: 'Gammal förvaltning', costCenterCode: '9000', isActive: false, teams: [] }),
    ])
    const sync = vi.spyOn(api.organisation, 'syncStatus')

    const wrapper = mount(OrganisationView)
    await flushPromises()

    const text = wrapper.text()
    expect(text).toContain('Kommunledningskontoret')
    expect(text).toContain('Digitalisering & AI')
    expect(text).toContain('Inactive')
    expect(text).toContain('No teams')
    expect(sync).not.toHaveBeenCalled() // the sync's status is for platform admins
    expect(text).not.toContain('Fetch now')
  })

  it('lets a platform admin sync now and says what changed', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-platform-admin'] })
    auth.status = 'authenticated'
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([department()])
    vi.spyOn(api.organisation, 'syncStatus').mockResolvedValue({
      enabled: true, lastRunAt: '2026-10-30T12:00:00Z', lastSuccessAt: '2026-10-30T12:00:00Z', consecutiveFailures: 0, lastError: null, lastResult: null, gateway: null, degraded: null, adminUrl: null,
    })
    const sync = vi.spyOn(api.organisation, 'sync').mockResolvedValue({ departments: 2, teams: 0 })

    const wrapper = mount(OrganisationView)
    await flushPromises()
    await wrapper.findAll('button').find((b) => b.text().includes('Fetch now'))!.trigger('click')
    await flushPromises()

    expect(sync).toHaveBeenCalledOnce()
  })

  it('shows each department\'s member groups and how its knowledge bases are shared (admin design §5.10)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser()
    auth.status = 'authenticated'
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([
      department({ name: 'Kommunledningskontoret', memberGroups: ['Kommunstyrelsen', 'KLK-alla'], sharing: { department: 2, selected: 1, municipality: 3, sharedWithDepartments: 4 } }),
      department({ name: 'Fritid', memberGroups: [] }),
    ])
    const wrapper = mount(OrganisationView)
    await flushPromises()
    const [first, second] = wrapper.findAll('tbody tr')
    expect(first!.text()).toContain('Kommunstyrelsen, KLK-alla')
    // One phrase (admin design §5.7; #1036): every knowledge base the department owns (2 kept to it + 4 shared), and how
    // many of those reach other departments (selected or everyone, 1 + 3). The API's three counts are disjoint.
    expect(first!.get('[data-test=knowledge-bases]').text()).toBe('6 own · 4 shared')
    expect(first!.text()).toContain(`${PLATFORM.organisation.sharedWith}: 4`)
    // No member groups is a grey dash, with the words for a screen reader.
    expect(second!.get('[data-test=no-member-groups] [aria-hidden=true]').text()).toBe('–')
    expect(second!.get('[data-test=no-member-groups] .sr-only').text()).toBe(PLATFORM.organisation.noMemberGroups)
    // A viewer gets no link to the gateway's administration.
    expect(wrapper.find('[data-test=gateway-page]').exists()).toBe(false)
  })

  it('links a platform admin to the gateway\'s Organisation page in a new tab when the sync knows an http(s) admin address', async () => {
    const status = (adminUrl: string | null) => ({
      enabled: true, lastRunAt: null, lastSuccessAt: null, consecutiveFailures: 0, lastError: null, lastResult: null, gateway: null, degraded: null, adminUrl,
    })
    for (const [adminUrl, href] of [
      ['https://gateway-admin.example.invalid/', 'https://gateway-admin.example.invalid/organisation'], ['javascript:alert(1)', null], [null, null],
    ] as const) {
      setActivePinia(createPinia())
      const auth = useAuthStore()
      auth.user = bffUser({ roles: ['rag-platform-admin'] })
      auth.status = 'authenticated'
      vi.spyOn(api.organisation, 'departments').mockResolvedValue([department()])
      vi.spyOn(api.organisation, 'syncStatus').mockResolvedValue(status(adminUrl))
      const wrapper = mount(OrganisationView)
      await flushPromises()
      const link = wrapper.find('[data-test=gateway-page]')
      if (href) {
        expect(link.attributes('href')).toBe(href)
        expect(link.attributes('target')).toBe('_blank')
        expect(link.attributes('rel')).toContain('noopener')
        expect(link.text()).toContain(PLATFORM.organisation.gatewayLink)
      } else {
        expect(link.exists()).toBe(false)
      }
      wrapper.unmount()
    }
  })
})
