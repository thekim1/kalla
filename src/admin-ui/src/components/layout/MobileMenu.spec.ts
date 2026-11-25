import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import type { NavGroup } from '@/router/nav'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import MobileMenu from './MobileMenu.vue'

const groups: NavGroup[] = [
  { id: 'home', label: 'Home', hideLabel: true, items: [{ name: 'home', label: 'Home', icon: 'home' }] },
  { id: 'content', label: 'Content', items: [{ name: 'collections', label: 'Knowledge bases', icon: 'book' }] },
]

describe('The phone Menu sheet (admin design §5.2)', () => {
  it('lets Home stand alone: its group heading is for assistive technology only, the others show', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', name: 'home', component: {} }, { path: '/collections', name: 'collections', component: {} }] })
    await router.push('/')
    const wrapper = mount(MobileMenu, {
      props: { open: true, groups, badgeCount: () => 0, updateAvailable: false, themes: [], sessionLeft: null, signingOut: false },
      global: { plugins: [router] },
    })
    expect(wrapper.get('#menu-home').classes()).toContain('sr-only')
    expect(wrapper.get('#menu-content').classes()).not.toContain('sr-only')
    expect(wrapper.get('[aria-labelledby=menu-home]').text()).toContain('Home')
  })
})
