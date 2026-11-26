/* eslint-disable vue/one-component-per-file -- one small host page per case */
import { describe, expect, it } from 'vitest'
import { defineComponent, h, nextTick } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import { IS_DAY, ONE_OF, useQueryFilters } from './useQueryFilters'

describe('useQueryFilters (layout rule 2)', () => {
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

  it('reads a value outside a key\'s set as its default, and drops a custom range once the period is no longer Custom', async () => {
    let api!: ReturnType<typeof useQueryFilters<'period' | 'from' | 'to' | 'state'>>
    const Page = defineComponent({
      setup: () => {
        api = useQueryFilters({ period: '30d', from: '', to: '', state: 'open' }, {
          period: ONE_OF(['7d', '30d', 'month', 'custom']), from: IS_DAY, to: IS_DAY, state: ONE_OF(['all', 'reviewed']),
        })
        return () => h('p')
      },
    })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/f', component: Page }] })
    await router.push('/f?period=foo&state=bogus&from=2026-01-01')
    mount(RouterView, { global: { plugins: [router] } })
    await flushPromises()
    // Not Custom: the from is dropped; the bad values read as their defaults.
    expect({ ...api.filters }).toEqual({ period: '30d', from: '', to: '', state: 'open' })
    expect(api.active.value).toEqual([])

    api.filters.period = 'custom'
    api.filters.from = '2026-01-01'
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ period: 'custom', from: '2026-01-01' })
    api.filters.period = '7d' // leaving Custom… takes its range along
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual({ period: '7d' })
    expect(api.active.value).toEqual(['period'])
  })

  it('moves focus to the first chip of the row on Clear filters, never to the body', async () => {
    let api!: ReturnType<typeof useQueryFilters<'rating'>>
    const Page = defineComponent({ setup: () => { api = useQueryFilters({ rating: '' }); return () => h('p') } })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/f', component: Page }] })
    await router.push('/f?rating=down')
    mount(RouterView, { global: { plugins: [router] } })
    await flushPromises()
    const row = document.createElement('form')
    row.innerHTML = '<button>x</button><select data-test="chip-select"><option>a</option></select>'
    document.body.append(row)
    api.clear(row)
    await nextTick()
    expect(document.activeElement).toBe(row.querySelector('select'))
    row.remove()
  })
})
