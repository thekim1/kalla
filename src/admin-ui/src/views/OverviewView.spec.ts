import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
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

describe('Översikt: your access (P0 checklist #9)', () => {
  it('shows the roles in Swedish and where each department came from', () => {
    signIn({
      roles: ['rag-viewer', 'offline_access'],
      departmentCodes: ['1000', '3000'],
      departmentSources: [{ code: '1000', source: 'claim' }, { code: '3000', source: 'group' }],
    })
    const wrapper = mount(OverviewView, { global: { stubs: { RouterLink: true } } })
    expect(wrapper.get('[data-test=roles]').text()).toBe('Läsbehörighet')
    const departments = wrapper.get('[data-test=departments]').text()
    expect(departments).toContain('1000')
    expect(departments).toContain('Från ditt konto')
    expect(departments).toContain('3000')
    expect(departments).toContain('Via medlemsgrupp')
  })

  it('tells a user without a rag-* role to ask for access', () => {
    signIn({ roles: [], departmentCodes: [], departmentSources: [] })
    const wrapper = mount(OverviewView)
    expect(wrapper.text()).toContain('Du har ingen roll i Kunskapsplattformen än.')
  })
})
