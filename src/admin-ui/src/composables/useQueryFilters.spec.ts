import { describe, expect, it } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import { useQueryFilters } from './useQueryFilters'

describe('useQueryFilters (admin design §5.12 Filters, B-80)', () => {
  async function page() {
    let api!: ReturnType<typeof useQueryFilters<'period' | 'rating'>>
    const Page = defineComponent({ setup: () => { api = useQueryFilters({ period: '30d', rating: '' }); return () => h('p') } })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/feedback', component: Page }, { path: '/other', component: { render: () => h('p') } }] })
    await router.push('/feedback?rating=down')
    mount(RouterView, { global: { plugins: [router] } })
    await flushPromises()
    return { router, api: () => api }
  }

  it('reads the filters from the address, and leaves a key at its default out of it', async () => {
    const { router, api } = await page()
    expect({ ...api().filters }).toEqual({ period: '30d', rating: 'down' })
    api().filters.period = '7d'
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ rating: 'down', period: '7d' })
    api().filters.period = '30d'
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ rating: 'down' })
  })

  it('replaces the address (no history entry), and follows it on back and forward', async () => {
    const { router, api } = await page()
    await router.push('/feedback?rating=up') // a link to the same page with another filter
    await flushPromises()
    expect(api().filters.rating).toBe('up')
    api().filters.period = 'month' // a change replaces: back goes to the address before it, not before the change
    await flushPromises()
    router.back()
    await flushPromises()
    await nextTick()
    expect(router.currentRoute.value.fullPath).toBe('/feedback?rating=down')
    expect({ ...api().filters }).toEqual({ period: '30d', rating: 'down' })
    router.forward()
    await flushPromises()
    expect({ ...api().filters }).toEqual({ period: 'month', rating: 'up' })
  })

  it('clears every filter back to its default, and lists the set ones', async () => {
    const { api } = await page()
    expect(api().active.value).toEqual(['rating'])
    api().clear()
    expect(api().active.value).toEqual([])
    expect({ ...api().filters }).toEqual({ period: '30d', rating: '' })
  })
})
