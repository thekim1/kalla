import { describe, expect, it } from 'vitest'
import { defineComponent, h } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import { useTabQuery } from './useTabQuery'

describe('useTabQuery (a tab in ?tab=)', () => {
  it('opens the tab an old value moved to, and rewrites the address to the tab\'s own name (#1049)', async () => {
    let tab!: ReturnType<typeof useTabQuery<'overview' | 'content' | 'quality'>>
    const Page = defineComponent({ setup: () => { tab = useTabQuery(['overview', 'content', 'quality'] as const, { files: 'content', sources: 'content' }); return () => h('p') } })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/c', component: Page }] })
    await router.push('/c?tab=files&x=1')
    mount(RouterView, { global: { plugins: [router] } })
    await flushPromises()
    expect(tab.value).toBe('content')
    expect(router.currentRoute.value.query).toEqual({ tab: 'content', x: '1' })

    await router.push('/c?tab=nonsense') // unknown, not an alias: the first tab, and the address is left as it is
    await flushPromises()
    expect(tab.value).toBe('overview')
    expect(router.currentRoute.value.query).toEqual({ tab: 'nonsense' })
  })
})
