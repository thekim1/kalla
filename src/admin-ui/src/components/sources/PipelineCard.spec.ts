import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory, createRouter } from 'vue-router'
import { api } from '@/api'
import type { Pipeline, Source } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import PipelineCard from './PipelineCard.vue'

const definition = { schema: 1, template: 'website', templateVersion: 1, params: { chunkTokens: 500 } }
const platformPipeline: Pipeline = {
  id: 'p1', name: 'website', templateId: 'website', departmentId: null, publishedVersionId: 'v1', version: '4',
  versions: [{ id: 'v1', number: 1, state: 'published', definition, publishedAt: '2026-10-01T10:00:00Z' }],
}
const source = { id: 's1', name: 'umea.se/bygga', collectionId: 'c1', pipelineId: 'p1' } as Source

function setup(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: {} }, { path: '/publications/:id', name: 'publication', component: {} }] })
  vi.spyOn(api.pipelines, 'get').mockResolvedValue(platformPipeline)
  vi.spyOn(api.templates, 'get').mockResolvedValue({
    id: 'website', version: 1, sourceType: 'crawl', deprecated: false, stages: [],
    paramsSchema: { properties: {
      chunkTokens: { type: 'integer', minimum: 200, maximum: 800, default: 400 },
      ocr: { enum: ['auto', 'force', 'off'], default: 'auto' },
      pathRules: { $ref: '#/$defs/pathRules', default: [] },
    } },
  })
  return router
}

describe('Bearbetning on the source page (admin design §6.6 step 1)', () => {
  it('edits the template parameters and plans a copy-on-write publication for a department user', async () => {
    const router = setup(['rag-department-admin'])
    const plan = vi.spyOn(api.pipelines, 'plan').mockResolvedValue({ publicationId: 'pub1', pipelineId: 'p2', draftVersionId: 'v9', draftNumber: 2, copiedOnWrite: true })
    const wrapper = mount(PipelineCard, { props: { source }, attachTo: document.body, global: { plugins: [router] } })
    await flushPromises()

    const card = wrapper.get('[data-test=pipeline]').text()
    expect(card).toContain('Textavsnittens längd (token)500')
    expect(card).toContain('Automatiskt')

    await wrapper.get('[data-test=edit-pipeline]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[data-test=copy-on-write]')).not.toBeNull()
    expect(document.body.textContent).toContain('Från 200 till 800.')
    const tokens = document.body.querySelector<HTMLInputElement>('#param-chunkTokens')!
    expect(tokens.value).toBe('500')
    tokens.value = 'många'
    tokens.dispatchEvent(new Event('input'))
    document.body.querySelector<HTMLButtonElement>('[data-test=plan]')!.click()
    await flushPromises()
    expect(document.body.textContent).toContain('Skriv ett heltal.')
    expect(plan).not.toHaveBeenCalled()

    tokens.value = '600'
    tokens.dispatchEvent(new Event('input'))
    document.body.querySelector<HTMLButtonElement>('[data-test=plan]')!.click()
    await flushPromises()
    expect(plan).toHaveBeenCalledWith('p1', { parameters: { chunkTokens: 600, ocr: 'auto', pathRules: [] }, version: '4', collectionId: 'c1' })
    expect(router.currentRoute.value.fullPath).toBe('/publications/pub1')
    wrapper.unmount()
  })

  it('shows the pipeline read-only to a reader', async () => {
    const router = setup(['rag-viewer'])
    const wrapper = mount(PipelineCard, { props: { source }, global: { plugins: [router] } })
    await flushPromises()
    expect(wrapper.text()).toContain('website')
    expect(wrapper.find('[data-test=edit-pipeline]').exists()).toBe(false)
  })
})
