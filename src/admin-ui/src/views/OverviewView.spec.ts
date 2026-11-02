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
  it('shows the roles in Swedish and where each department came from', () => {
    signIn({
      roles: ['rag-viewer', 'offline_access'],
      departmentCodes: ['1000', '3000'],
      departmentSources: [{ code: '1000', source: 'claim' }, { code: '3000', source: 'group' }],
    })
    vi.spyOn(api, 'overview').mockResolvedValue({ hasCollections: true, setup: [] })
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: true } } })
    expect(wrapper.get('[data-test=roles]').text()).toBe('Read access')
    const departments = wrapper.get('[data-test=departments]').text()
    expect(departments).toContain('1000')
    expect(departments).toContain('From your account')
    expect(departments).toContain('3000')
    expect(departments).toContain('Through a member group')
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
