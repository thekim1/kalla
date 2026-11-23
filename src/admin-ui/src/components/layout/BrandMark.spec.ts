import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import BrandMark from './BrandMark.vue'

function mark(logoUrl: string | null | undefined, size?: 'sm' | 'md' | 'lg') {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ logoUrl })
  return { wrapper: mount(BrandMark, { props: size ? { size } : {} }), auth }
}

describe('the brand mark: the gateway organisation logo, else the built-in icon', () => {
  it('shows the logo kalla-admin serves when the gateway has one', () => {
    const { wrapper } = mark('/bff/branding/logo?v=abc')
    const img = wrapper.get('[data-test=brand-logo]')
    expect(img.attributes('src')).toBe('/bff/branding/logo?v=abc')
    expect(img.attributes('alt')).toBe('')
    expect(img.classes()).toContain('h-9')
    expect(img.classes()).toContain('w-auto') // a wide wordmark keeps its shape
    expect(wrapper.find('[data-test=brand-icon]').exists()).toBe(false)
  })

  it('falls back to the built-in icon without a logo, or from a gateway that does not offer one', () => {
    for (const logoUrl of [null, undefined]) {
      const { wrapper } = mark(logoUrl)
      expect(wrapper.find('[data-test=brand-logo]').exists()).toBe(false)
      expect(wrapper.get('[data-test=brand-icon]').attributes('aria-hidden')).toBe('true')
    }
  })

  it('falls back to the built-in icon when the logo does not load, and tries a new one again', async () => {
    const { wrapper, auth } = mark('/bff/branding/logo?v=abc', 'lg')
    await wrapper.get('[data-test=brand-logo]').trigger('error')
    expect(wrapper.find('[data-test=brand-logo]').exists()).toBe(false)
    expect(wrapper.get('[data-test=brand-icon]').classes()).toContain('h-11')

    auth.user = bffUser({ logoUrl: '/bff/branding/logo?v=def' })
    await wrapper.vm.$nextTick()
    expect(wrapper.get('[data-test=brand-logo]').attributes('src')).toBe('/bff/branding/logo?v=def')
  })
})
