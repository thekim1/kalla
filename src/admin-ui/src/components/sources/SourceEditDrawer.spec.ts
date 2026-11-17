import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import { collectionUser } from '@/test/fixtures'
import type { Collection, Source } from '@/api/types'
import SourceEditDrawer from './SourceEditDrawer.vue'

const collection = { id: 'c1', classification: 'public' } as Collection
const source: Source = {
  id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'public', state: 'active', schedule: '0 1 * * 6', pipelineId: 'p1',
  containsThirdPartyText: true, updatedAt: '', updatedBy: null, version: '7',
  settings: {
    startUrls: ['https://www.umea.se/bygga'], include: ['www.umea.se/bygga/**'], exclude: ['**/sok?*'], maxPages: 10000, maxDepth: 10, piiMode: 'hold',
    demote: [{ pattern: 'www.umea.se/bygga/**/arkiv/**', factor: 0.5 }], conditionalGet: true,
  },
}

function input(selector: string, value: string): void {
  const el = document.body.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector)!
  el.value = value
  el.dispatchEvent(new Event('input'))
}
const click = (test: string) => document.body.querySelector<HTMLButtonElement>(`[data-test=${test}]`)!.click()

async function open() {
  const wrapper = mount(SourceEditDrawer, { props: { open: false, source, collection }, attachTo: document.body })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

describe('Edit source (admin design §5.5.3, V25)', () => {
  it('shows the impact of a scope change and saves only with Save anyway, sending the token and keeping other settings', async () => {
    const impact = vi.spyOn(api.sources, 'impact').mockResolvedValue({
      effects: ['scope'], liveDocuments: 840, impactToken: 'tok',
      usedBy: [
        collectionUser({ id: 'a1', name: 'Bygglovsassistenten', classification: 'public' }),
        collectionUser({ id: 'a2', name: 'Kundtjänst bygg', classification: 'public', published: false, draft: true }),
        collectionUser({ id: 'a3', name: 'GK check', classification: 'public', state: 'archived', published: true }),
      ],
    })
    const update = vi.spyOn(api.sources, 'update').mockResolvedValue({ ...source, version: '8' })
    const wrapper = await open()

    expect(document.body.querySelector<HTMLTextAreaElement>('#edit-demote')!.value).toBe('www.umea.se/bygga/**/arkiv/** 0,5')
    input('#edit-exclude', '**/sok?*\n**/gammalt/**')
    input('#edit-sitemaps', 'https://www.umea.se/sitemap.xml')
    input('#edit-demote', 'www.umea.se/bygga/**/arkiv/** 0,3\nwww.umea.se/bygga/**/nyheter/**')
    click('save-source')
    await flushPromises()

    const settings = {
      ...source.settings, sitemaps: ['https://www.umea.se/sitemap.xml'], exclude: ['**/sok?*', '**/gammalt/**'],
      demote: [{ pattern: 'www.umea.se/bygga/**/arkiv/**', factor: 0.3 }, { pattern: 'www.umea.se/bygga/**/nyheter/**', factor: 0.5 }],
    }
    expect(impact).toHaveBeenCalledWith('s1', { settings })
    expect(update).not.toHaveBeenCalled()
    const text = document.body.querySelector('[data-test=impact]')!.textContent!.replace(/\u00a0/g, ' ')
    expect(text).toContain('Of the source’s 840 searchable documents')
    expect(text).toContain('Bygglovsassistenten, Kundtjänst bygg (draft), GK check (archived)')
    expect(document.body.querySelector('[data-test=save-source]')!.textContent).toContain('Save anyway')

    click('save-source')
    await flushPromises()
    expect(update).toHaveBeenCalledWith('s1', {
      name: 'umea.se/bygga', classification: 'public', settings, schedule: '0 1 * * 6', version: '7', impactToken: 'tok',
    })
    expect(wrapper.emitted('saved')?.[0]).toEqual([{ ...source, version: '8' }])
    wrapper.unmount()
  })

  it('shows that a fetch change fetches every page again and saves it with Save anyway, without a token (connectors design §12.3)', async () => {
    const browserSource: Source = {
      ...source, type: 'url-list', settings: { urls: ['https://app.example.se/'], fetchMode: 'browser', readySelector: '#app h1', scrollToLoad: true, piiMode: 'hold' },
    }
    const impact = vi.spyOn(api.sources, 'impact').mockResolvedValue({ effects: ['behaviour', 'refetch'], liveDocuments: 12, impactToken: null, usedBy: [] })
    const update = vi.spyOn(api.sources, 'update').mockResolvedValue(browserSource)
    const wrapper = mount(SourceEditDrawer, { props: { open: false, source: browserSource, collection }, attachTo: document.body })
    await wrapper.setProps({ open: true })
    await flushPromises()

    expect(document.body.querySelector<HTMLSelectElement>('#edit-fetch-mode')!.value).toBe('browser')
    expect(document.body.querySelector<HTMLInputElement>('#edit-ready-selector')!.value).toBe('#app h1')
    expect(document.body.querySelector<HTMLInputElement>('[data-test=scroll-to-load] input')!.checked).toBe(true)

    const mode = document.body.querySelector<HTMLSelectElement>('#edit-fetch-mode')!
    mode.value = 'http'
    mode.dispatchEvent(new Event('change'))
    await flushPromises()
    expect(document.body.querySelector('#edit-ready-selector')).toBeNull()
    click('save-source')
    await flushPromises()

    const settings = { urls: ['https://app.example.se/'], piiMode: 'hold' }
    expect(impact).toHaveBeenCalledWith('s1', { settings })
    expect(update).not.toHaveBeenCalled()
    expect(document.body.querySelector('[data-test=impact-refetch]')!.textContent).toContain('every page of the source is fetched again now')
    expect(document.body.querySelector('[data-test=save-source]')!.textContent).toContain('Save anyway')

    click('save-source')
    await flushPromises()
    expect(update).toHaveBeenCalledWith('s1', { name: 'umea.se/bygga', classification: 'public', settings, schedule: '0 1 * * 6', version: '7' })
    wrapper.unmount()
  })

  it('saves a change without scope effect at once, and refuses a bad Demote line', async () => {
    vi.spyOn(api.sources, 'impact').mockResolvedValue({ effects: ['cosmetic'], liveDocuments: 840, impactToken: null, usedBy: [] })
    const update = vi.spyOn(api.sources, 'update').mockResolvedValue(source)
    const wrapper = await open()

    input('#edit-demote', 'www.umea.se/arkiv/** 2')
    click('save-source')
    await flushPromises()
    expect(document.body.textContent).toContain('optionally followed by a factor between 0 and 1')
    expect(update).not.toHaveBeenCalled()

    input('#edit-demote', '')
    input('#edit-name', 'Bygga och bo')
    click('save-source')
    await flushPromises()
    expect(update).toHaveBeenCalledWith('s1', expect.objectContaining({ name: 'Bygga och bo', version: '7' }))
    expect(update.mock.calls[0]![1]).not.toHaveProperty('impactToken')
    wrapper.unmount()
  })
})
