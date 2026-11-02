import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import OverviewView from './OverviewView.vue'

function signIn(overrides: Parameters<typeof bffUser>[0]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser(overrides)
  auth.status = 'authenticated'
}

describe('Overview: your access (P0 checklist #9)', () => {
  it('shows the roles, each department by name, and where each department came from', async () => {
    signIn({
      roles: ['rag-viewer', 'offline_access'],
      departmentCodes: ['1000', '3000'],
      departmentSources: [{ code: '1000', source: 'claim' }, { code: '3000', source: 'group' }],
    })
    vi.spyOn(api, 'overview').mockResolvedValue({ hasCollections: true, setup: [] })
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([
      { id: 'd1', name: 'Bygg och miljö', costCenterCode: '1000', isActive: true, syncedAt: '2026-11-01T10:00:00Z', teams: [] },
    ])
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(wrapper.get('[data-test=roles]').text()).toBe('Read access')
    const items = wrapper.findAll('[data-test=departments] li').map((li) => li.text())
    expect(items[0]).toContain('Bygg och miljö')
    expect(items[0]).toContain('1000')
    expect(items[0]).toContain('From your account')
    expect(items[1]).toContain('Unknown department (code 3000)')
    expect(items[1]).toContain('Through a member group')
  })

  it('tells a user without a rag-* role to ask for access', () => {
    signIn({ roles: [], departmentCodes: [], departmentSources: [] })
    const wrapper = mount(OverviewView)
    expect(wrapper.text()).toContain('You have no role in the Knowledge platform yet.')
  })

  it('shows Get started for each unfinished collection, and the empty state with Create knowledge base', async () => {
    signIn({ roles: ['rag-editor'], departmentCodes: ['1000'], departmentSources: [{ code: '1000', source: 'claim' }] })
    const overview = vi.spyOn(api, 'overview').mockResolvedValue({
      hasCollections: true,
      setup: [{
        collectionId: 'c1', name: 'Bygglov', sources: 1, firstSyncDone: false, syncRunning: true, testQuestions: 0, evaluated: false, evaluating: false,
        published: false, assistantId: null, assistantPublished: false, available: false, done: false,
      }],
    })
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } })
    await flushPromises()
    expect(wrapper.get('[data-test=setup]').text()).toContain('Get started: Bygglov')
    expect(wrapper.get('[data-test=step-source]').attributes('data-state')).toBe('done')
    expect(wrapper.get('[data-test=step-sync]').attributes('data-state')).toBe('active')
    expect(wrapper.get('[data-test=step-sync]').text()).toContain('Fetching')
    expect(wrapper.get('[data-test=step-available]').attributes('data-state')).toBe('todo')

    overview.mockResolvedValue({ hasCollections: false, setup: [] })
    const empty = mount(OverviewView, { global: { stubs: { RouterLink: true } } })
    await flushPromises()
    expect(empty.get('[data-test=empty]').text()).toContain('You have no knowledge bases yet.')
    expect(empty.find('[data-test=create]').exists()).toBe(true)
  })
})
