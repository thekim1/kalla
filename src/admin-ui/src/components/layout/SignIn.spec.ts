import { describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { SIGN_IN } from '@/utils/labels'
import SignIn from './SignIn.vue'

async function signInPage(flags: { configured: boolean; open: boolean; logoUrl?: string | null }) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({
    isAuthenticated: false,
    name: null,
    logoUrl: flags.logoUrl ?? null,
    signIn: { configured: flags.configured },
    setup: { open: flags.open },
  })
  auth.status = 'unauthenticated'
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: {} }, { path: '/setup', name: 'setup', component: {} }] })
  await router.push('/')
  const wrapper = mount(SignIn, { global: { plugins: [router] } })
  return { wrapper, router }
}

describe('the sign-in page in setup mode (identity design §19)', () => {
  it('offers only the sign-in when sign-in is set up and setup is closed', async () => {
    const { wrapper } = await signInPage({ configured: true, open: false })
    expect(wrapper.find('[data-test=sign-in]').exists()).toBe(true)
    expect(wrapper.find('[data-test=set-up]').exists()).toBe(false)
    expect(wrapper.find('[data-test=not-configured]').exists()).toBe(false)
  })

  it('leads to the wizard, and offers no sign-in, on a first run', async () => {
    const { wrapper, router } = await signInPage({ configured: false, open: true })
    expect(wrapper.find('[data-test=sign-in]').exists()).toBe(false)
    const setUp = wrapper.get('[data-test=set-up]')
    expect(setUp.text()).toContain(SIGN_IN.setUp)
    expect(setUp.attributes('href')).toBe('/setup')
    await setUp.trigger('click')
    await flushPromises()
    expect(router.currentRoute.value.name).toBe('setup')
  })

  it('keeps the sign-in beside the wizard while a re-open is active', async () => {
    const { wrapper } = await signInPage({ configured: true, open: true })
    expect(wrapper.find('[data-test=set-up]').exists()).toBe(true)
    expect(wrapper.find('[data-test=sign-in]').exists()).toBe(true)
  })

  it('explains that sign-in is not set up when the setup window has ended', async () => {
    const { wrapper } = await signInPage({ configured: false, open: false })
    expect(wrapper.get('[data-test=not-configured]').text()).toContain(SIGN_IN.notConfigured)
    expect(wrapper.find('[data-test=sign-in]').exists()).toBe(false)
    expect(wrapper.find('[data-test=set-up]').exists()).toBe(false)
    expect(wrapper.find('a').exists()).toBe(false)
  })
})

describe('the sign-in page brand', () => {
  it("shows the gateway organisation's logo before sign-in, else the built-in mark", async () => {
    const withLogo = (await signInPage({ configured: true, open: false, logoUrl: '/bff/branding/logo?v=abc' })).wrapper
    expect(withLogo.get('[data-test=brand-logo]').attributes('src')).toBe('/bff/branding/logo?v=abc')
    const without = (await signInPage({ configured: true, open: false })).wrapper
    expect(without.find('[data-test=brand-logo]').exists()).toBe(false)
    expect(without.find('[data-test=brand-icon]').exists()).toBe(true)
  })
})
