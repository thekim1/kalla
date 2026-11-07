import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { collection as collectionFixture } from '@/test/fixtures'
import type { Collection, Job, PreflightResult, PreviewOutput, Source, StagePreviewResult } from '@/api/types'
import SourceDrawer from './SourceDrawer.vue'

const collection: Collection = collectionFixture({ classification: 'public' })

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

describe('Add source (admin design §5.4 steps 3, 4, 6)', () => {
  it('checks a crawl source; a failed check blocks saving and says why in plain words', async () => {
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
        maxPages: 500, maxDepth: 10, piiMode: 'hold',
      },
    })
    const text = document.body.textContent!
    expect(text).toContain('The website’s robots.txt does not allow fetching the address.')
    expect(text).toContain('About 1')
    expect(text).toContain('The check found something you must fix before you save.')
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
    expect(document.body.textContent).toContain('Enter at least one address that starts with http:// or https://.')
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

  it('previews up to 5 addresses on the default pipeline: Before / After, the stage strip and a stage\'s output', async () => {
    const list = vi.spyOn(api.pipelines, 'list').mockResolvedValue([
      { id: 'p-default', name: 'crawl-default', templateId: 'crawl-default', departmentId: null, publishedVersionId: 'v1', isDefault: true },
      { id: 'p-own', name: 'crawl-own', templateId: 'crawl-default', departmentId: 'd1', publishedVersionId: 'v2', isDefault: false },
    ])
    const preview = vi.spyOn(api.pipelines, 'preview').mockResolvedValue({ jobId: 'pj' })
    const result: StagePreviewResult = {
      expiresAt: '2026-11-03T10:00:00Z',
      documents: [
        {
          url: 'https://www.umea.se/bygga/altan', documentId: null,
          stages: [
            { stage: 'fetch', state: 'succeeded', durationMs: 120 },
            { stage: 'parse', state: 'succeeded', durationMs: 40, cacheHit: true, output: 'previews/pj/0/parse.json' },
            { stage: 'chunk', state: 'succeeded', durationMs: 5, output: 'previews/pj/0/chunk.json' },
          ],
          siteProfile: { host: 'www.umea.se', origin: 'preset', presetId: 'sitevision', before: 'previews/pj/0/site-profile-before.json', after: 'previews/pj/0/parse.json' },
        },
        { url: 'https://www.umea.se/saknas', stages: [{ stage: 'fetch', state: 'failed', error: 'parse.empty' }] },
      ],
    }
    vi.spyOn(api.jobs, 'get').mockResolvedValue({ ...job({ state: 'pass', checks: [] }), id: 'pj', kind: 'stage-preview', result } as Job<unknown>)
    const outputs: Record<string, PreviewOutput<unknown>> = {
      before: { stage: 'parse', truncated: false, value: { blocks: [{ kind: 'paragraph', text: 'Meny Logga in Kontakt' }, { kind: 'paragraph', text: 'Bygga altan' }] } },
      after: {
        stage: 'parse', truncated: false,
        value: { blocks: [{ kind: 'heading', text: 'Bygga altan', level: 1 }, { kind: 'paragraph', text: 'Meny', layer: 'furniture' }], breadcrumb: ['Bygga', 'Altan'], visibleDate: '2026-10-01' },
      },
      chunk: { stage: 'chunk', truncated: false, value: { chunks: [{ ordinal: 0, prefix: 'Bygga › Altan', headingPath: 'Bygga altan', text: 'Du behöver bygglov om…', tokenCount: 42 }] } },
    }
    const output = vi.spyOn(api.previews, 'output').mockImplementation((_job, _doc, stage) => Promise.resolve(outputs[stage]) as never)
    const wrapper = await open()

    input('#source-urls', ['a', 'b', 'c', 'd', 'e', 'f'].map((p) => `https://www.umea.se/${p}`).join('\n'))
    await flushPromises()
    expect(document.body.textContent).toContain('Only the first 5 addresses are previewed.')
    click('preview')
    await flushPromises()

    expect(list).toHaveBeenCalledWith({ collectionId: 'c1', sourceType: 'crawl' })
    expect(preview).toHaveBeenCalledWith('p-default', { collectionId: 'c1', urls: ['a', 'b', 'c', 'd', 'e'].map((p) => `https://www.umea.se/${p}`) })
    expect(output).toHaveBeenCalledWith('pj', 0, 'before')
    expect(output).toHaveBeenCalledWith('pj', 0, 'after')
    const text = () => document.body.textContent!
    expect(text()).toContain('Site profile for www.umea.se: the preset sitevision')
    expect(document.body.querySelector('[data-test=text-before]')!.textContent).toContain('Meny Logga in Kontakt')
    const after = document.body.querySelector('[data-test=text-after]')!.textContent!
    expect(after).toContain('Bygga altan')
    expect(after).not.toContain('Meny') // furniture is not main text
    expect(text()).toContain('Bygga › Altan')
    const strip = document.body.querySelector('[data-test=stage-strip]')!.textContent!
    expect(strip).toContain('Read')
    expect(strip).toContain('reused')

    const chunkButton = [...document.body.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Passages')!
    chunkButton.click()
    await flushPromises()
    expect(output).toHaveBeenCalledWith('pj', 0, 'chunk')
    expect(document.body.querySelector('[data-test=output-chunk]')!.textContent).toContain('1 passage, 42 tokens')

    document.body.querySelector<HTMLButtonElement>('#preview-docs-tab-d1')!.click()
    await flushPromises()
    expect(text()).toContain('No text was left after the page was read and cleaned.')

    // A changed address makes the preview stale.
    input('#source-urls', 'https://www.umea.se/annan')
    await flushPromises()
    expect(document.body.querySelector('[data-test=preview-result]')).toBeNull()
    wrapper.unmount()
  })

  it('shows the browser settings only with a browser and saves them with a URL list (admin design §5.5.3)', async () => {
    const create = vi.spyOn(api.collections, 'addSource').mockResolvedValue({ id: 's2', name: 'Appen' } as Source)
    vi.spyOn(api.sources, 'sync').mockResolvedValue({ jobId: 'j', runId: null, alreadyRunning: false, kind: 'sync' })
    const wrapper = await open()

    document.body.querySelector<HTMLInputElement>('[data-test=type-url-list] input')!.click()
    await flushPromises()
    const mode = document.body.querySelector<HTMLSelectElement>('#source-fetch-mode')!
    expect(mode.value).toBe('http')
    expect(document.body.querySelector('#source-ready-selector')).toBeNull()
    expect(document.body.querySelector('[data-test=scroll-to-load]')).toBeNull()

    mode.value = 'browser'
    mode.dispatchEvent(new Event('change'))
    await flushPromises()
    expect(document.body.textContent).toContain('Content is ready when this element has text')
    expect(document.body.textContent).toContain('not a container that shows “Loading…”')
    expect(document.body.textContent).toContain('For pages that load more content as you scroll. Slower.')
    input('#source-ready-selector', ' #app h1 ')
    document.body.querySelector<HTMLInputElement>('[data-test=scroll-to-load] input')!.click()
    input('#source-name', 'Appen')
    input('#source-urls', 'https://app.example.se/')
    click('save')
    await flushPromises()
    expect(create).toHaveBeenCalledWith('c1', {
      type: 'url-list', name: 'Appen', classification: 'public',
      settings: { urls: ['https://app.example.se/'], piiMode: 'hold', fetchMode: 'browser', readySelector: '#app h1', scrollToLoad: true },
    })
    wrapper.unmount()
  })

  it('shows the raw and rendered words per page and switches to a browser when the site seems to need JavaScript', async () => {
    const preflight = vi.spyOn(api.sources, 'preflight').mockResolvedValue({ jobId: 'j1' })
    vi.spyOn(api.jobs, 'get').mockResolvedValue(job({
      state: 'warn', estimatedPages: 12, cms: null,
      checks: [
        { check: 'reachability', state: 'pass', code: 'http.200', target: 'https://app.example.se/' },
        { check: 'render', state: 'warn', code: 'render.suggested', target: 'https://app.example.se/', facts: { rawWords: 3, renderedWords: 1240 } },
        { check: 'render', state: 'pass', code: 'render.compared', target: 'https://app.example.se/om', facts: { rawWords: 400, renderedWords: 410 } },
      ],
    }) as Job<unknown>)
    const wrapper = await open()

    input('#source-urls', 'https://app.example.se/')
    click('check')
    await flushPromises()

    const checks = document.body.querySelector('[data-test=checks]')!.textContent!.replace(/\u00a0/g, ' ')
    expect(checks).toContain('The page holds much less text without JavaScript than with it.')
    expect(checks).toContain('3 words without JavaScript, 1 240 with JavaScript')
    expect(checks).toContain('400 words without JavaScript, 410 with JavaScript')
    expect(checks).toContain('Info')
    expect(document.body.querySelector('[data-test=needs-javascript]')!.textContent).toContain('This site seems to need JavaScript.')

    click('use-browser')
    await flushPromises()
    expect(document.body.querySelector<HTMLSelectElement>('#source-fetch-mode')!.value).toBe('browser')
    expect(document.body.querySelector('#source-ready-selector')).not.toBeNull()
    expect(document.body.querySelector('[data-test=needs-javascript]')).toBeNull()

    click('check')
    await flushPromises()
    expect(preflight).toHaveBeenLastCalledWith(expect.objectContaining({ settings: expect.objectContaining({ startUrls: ['https://app.example.se/'], fetchMode: 'browser' }) }))
    wrapper.unmount()
  })

  it('says once that no renderer is set up, as information', async () => {
    vi.spyOn(api.sources, 'preflight').mockResolvedValue({ jobId: 'j1' })
    vi.spyOn(api.jobs, 'get').mockResolvedValue(job({ state: 'pass', checks: [{ check: 'render', state: 'pass', code: 'render.not_configured' }] }) as Job<unknown>)
    const wrapper = await open()
    input('#source-urls', 'https://app.example.se/')
    click('check')
    await flushPromises()
    const checks = document.body.querySelector('[data-test=checks]')!.textContent!
    expect(checks).toContain('No browser for rendering pages is set up on the platform')
    expect(checks).toContain('Info')
    expect(document.body.querySelector('[data-test=render-words]')).toBeNull()
    expect(document.body.querySelector('[data-test=needs-javascript]')).toBeNull()
    wrapper.unmount()
  })

  it('says so when no pipeline can process the type', async () => {
    vi.spyOn(api.pipelines, 'list').mockResolvedValueOnce([])
    const wrapper = await open()
    input('#source-urls', 'https://www.umea.se/bygga')
    click('preview')
    await flushPromises()
    expect(document.body.textContent).toContain('There is no processing for this source type yet.')
    wrapper.unmount()
  })

  it('creates an upload source without a sync: its files are uploaded on its page', async () => {
    const saved = { id: 's9', name: 'Blanketter' } as Source
    const create = vi.spyOn(api.collections, 'addSource').mockResolvedValue(saved)
    const sync = vi.spyOn(api.sources, 'sync')
    const wrapper = await open()
    document.body.querySelector<HTMLInputElement>('[data-test=type-upload] input')!.click()
    await flushPromises()
    input('#source-name', 'Blanketter')
    click('save')
    await flushPromises()
    expect(create).toHaveBeenCalledWith('c1', { type: 'upload', name: 'Blanketter', classification: 'public', settings: { piiMode: 'hold' } })
    expect(sync).not.toHaveBeenCalled()
    expect(wrapper.emitted('saved')?.[0]).toEqual([saved, false])
    wrapper.unmount()
  })
})
