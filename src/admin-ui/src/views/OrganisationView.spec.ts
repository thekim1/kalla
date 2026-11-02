import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { bffUser, department } from '@/test/fixtures'
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
      enabled: true, lastRunAt: '2026-10-30T12:00:00Z', lastSuccessAt: '2026-10-30T12:00:00Z', consecutiveFailures: 0, lastError: null, lastResult: null,
    })
    const sync = vi.spyOn(api.organisation, 'sync').mockResolvedValue({ departments: 2, teams: 0 })

    const wrapper = mount(OrganisationView)
    await flushPromises()
    await wrapper.findAll('button').find((b) => b.text().includes('Fetch now'))!.trigger('click')
    await flushPromises()

    expect(sync).toHaveBeenCalledOnce()
  })
})
