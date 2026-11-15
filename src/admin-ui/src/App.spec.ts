import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory } from 'vue-router'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import { createAppRouter } from '@/router'
import { bffUser } from '@/test/fixtures'
import { SETUP_WIZARD, SHELL, SIGN_IN } from '@/utils/labels'
import App from './App.vue'

async function open(path: string) {
  const pinia = createPinia()
  setActivePinia(pinia)
  const router = createAppRouter(createMemoryHistory())
  await router.push(path)
  await router.isReady()
  const wrapper = mount(App, { global: { plugins: [pinia, router] } })
  await flushPromises()
  return wrapper
}

describe('the app before sign-in in setup mode (admin design §5.11)', () => {
  it('shows /setup in its own frame, without the sign-in page or the menu', async () => {
    vi.spyOn(api.bff, 'user').mockResolvedValue(bffUser({ isAuthenticated: false, name: null, signIn: { configured: false }, setup: { open: true } }))
    vi.spyOn(api.setup, 'state').mockRejectedValue(new ApiError(401, 'GET', { code: 'setup_session_required' }))
    const wrapper = await open('/setup')
    expect(wrapper.find('[data-test=setup-wizard]').exists()).toBe(true)
    expect(wrapper.get('h1').text()).toBe(SETUP_WIZARD.title)
    expect(wrapper.text()).not.toContain(SIGN_IN.title)
    expect(wrapper.find(`nav[aria-label="${SHELL.mainNav}"]`).exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows the sign-in page, with the way to the wizard, on any other page', async () => {
    vi.spyOn(api.bff, 'user').mockResolvedValue(bffUser({ isAuthenticated: false, name: null, signIn: { configured: false }, setup: { open: true } }))
    const wrapper = await open('/collections')
    expect(wrapper.get('h1').text()).toBe(SIGN_IN.title)
    expect(wrapper.find('[data-test=set-up]').exists()).toBe(true)
    wrapper.unmount()
  })
})
