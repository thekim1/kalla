import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Collection, Job, PreflightResult, Source } from '@/api/types'
import SourceDrawer from './SourceDrawer.vue'

const collection: Collection = {
  id: 'c1', slug: 'bygglov', name: 'Bygglov', departmentId: 'd1', classification: 'public', publishedAt: null, ownerTeamId: null,
  embeddingProfileId: 'p', retrievalSettings: {}, usedBy: [], updatedAt: '2026-11-01T10:00:00Z', updatedBy: null, version: '1',
}

function job(result: PreflightResult): Job<PreflightResult> {
  return { id: 'j1', class: 'preview', kind: 'preflight', state: 'succeeded', enqueuedAt: '', completedAt: '', runId: null, result, lastError: null }
}

function input(selector: string, value: string): void {
  const el = document.body.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector)!
  el.value = value
  el.dispatchEvent(new Event('input'))
}

const click = (test: string) => document.body.querySelector<HTMLButtonElement>(`[data-test=${test}]`)!.click()

async function open() {
  setActivePinia(createPinia())
  const wrapper = mount(SourceDrawer, { props: { open: false, collection }, attachTo: document.body })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

describe('Lägg till källa (admin design §5.4 steps 3, 4, 6)', () => {
  it('checks a crawl source; a failed check blocks saving and says why in Swedish', async () => {
    const preflight = vi.spyOn(api.sources, 'preflight').mockResolvedValue({ jobId: 'j1' })
    vi.spyOn(api.jobs, 'get').mockResolvedValue(job({
      state: 'fail', estimatedPages: 1200, cms: null,
      checks: [{ check: 'robots', state: 'fail', code: 'robots.disallow', target: 'https://www.umea.se/bygga' }, { check: 'sitemaps', state: 'pass', code: 'sitemap.found' }],
    }) as Job<unknown>)
    const create = vi.spyOn(api.collections, 'addSource')
    const wrapper = await open()

    input('#source-urls', 'https://www.umea.se/bygga')
    click('check')
    await flushPromises()

    expect(preflight).toHaveBeenCalledWith({
      collectionId: 'c1', type: 'crawl',
      settings: {
        startUrls: ['https://www.umea.se/bygga'], include: [], exclude: ['**/sok?*', '**/search?*', '**?*sort=*', '**?*print=*', '**/login*', '**/logout*'],
        maxPages: 10000, maxDepth: 10, piiMode: 'hold',
      },
    })
    const text = document.body.textContent!
    expect(text).toContain('Webbplatsens robots.txt förbjuder hämtning av adressen.')
    expect(text).toContain('Ungefär 1')
    expect(text).toContain('Kontrollen hittade något som måste rättas innan du sparar.')
    input('#source-name', 'umea.se/bygga')
    click('save')
    await flushPromises()
    expect(create).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('saves a URL list and starts its first sync', async () => {
    const saved = { id: 's1', name: 'Taxor' } as Source
    const create = vi.spyOn(api.collections, 'addSource').mockResolvedValue(saved)
    const sync = vi.spyOn(api.sources, 'sync').mockResolvedValue({ jobId: 'j', runId: null, alreadyRunning: false, kind: 'sync' })
    const wrapper = await open()

    document.body.querySelector<HTMLInputElement>('[data-test=type-url-list] input')!.click()
    await flushPromises()
    input('#source-name', 'Taxor')
    input('#source-urls', 'https://www.umea.se/taxa\nftp://fel')
    click('save')
    await flushPromises()
    expect(document.body.textContent).toContain('Skriv minst en adress som börjar med http:// eller https://.')
    expect(create).not.toHaveBeenCalled()

    input('#source-urls', 'https://www.umea.se/taxa\n\nhttps://www.umea.se/avgifter ')
    click('save')
    await flushPromises()
    expect(create).toHaveBeenCalledWith('c1', {
      type: 'url-list', name: 'Taxor', classification: 'public', settings: { urls: ['https://www.umea.se/taxa', 'https://www.umea.se/avgifter'], piiMode: 'hold' },
    })
    expect(sync).toHaveBeenCalledWith('s1')
    expect(wrapper.emitted('saved')?.[0]).toEqual([saved, true])
    wrapper.unmount()
  })
})
