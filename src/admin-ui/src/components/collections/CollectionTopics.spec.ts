import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Pipeline, Publication, Source, SourceTopicSuggestions, Topic } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { TOPICS } from '@/utils/labels'
import CollectionTopics from './CollectionTopics.vue'

const sources = [{ id: 's1', name: 'umea.se' }, { id: 's2', name: 'Filer' }]
const topics: Topic[] = [
  { tag: 'bygga', documents: 412, rules: [{ sourceId: 's1', sourceName: 'umea.se', on: 'path', match: '/bygga/' }], pending: false },
  { tag: 'protokoll', documents: 0, rules: [
    { sourceId: 's1', sourceName: 'umea.se', on: 'title-regex', match: 'Protokoll' },
    { sourceId: 's2', sourceName: 'Filer', on: 'mediaType', match: 'application/pdf' },
  ], pending: false },
  { tag: 'kommun', documents: null, rules: [{ sourceId: 's1', sourceName: 'umea.se', on: 'path', match: '/kommun/' }], pending: true },
]
const web: SourceTopicSuggestions = {
  sourceId: 's1', sourceName: 'umea.se', kind: 'website', under: null, sections: [
    { path: '/bygga/', documents: 612, title: 'Bygga, bo och miljö', suggestedTag: 'bygga', coveredBy: ['bygga'] },
    { path: '/utbildning/', documents: 488, title: 'Utbildning och barnomsorg', suggestedTag: 'utbildning', coveredBy: [] },
    { path: '/omsorg/', documents: 401, title: null, suggestedTag: 'omsorg', coveredBy: [] },
  ],
}
const files: SourceTopicSuggestions = {
  sourceId: 's2', sourceName: 'Filer', kind: 'upload', under: null, sections: [{ path: 'HR/', documents: 9, title: null, suggestedTag: 'hr', coveredBy: [] }],
}

function setup(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
  vi.spyOn(api.collections, 'topics').mockResolvedValue({ collectionId: 'c1', topics })
  return vi.spyOn(api.collections, 'topicSuggestions').mockResolvedValue({ collectionId: 'c1', sources: [files, web] })
}

const mountTab = (props: Record<string, unknown> = { collectionId: 'c1', sources }) =>
  mount(CollectionTopics, { props: props as never, attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })

function row(wrapper: ReturnType<typeof mountTab>, path: string) {
  return wrapper.get(`[data-test=topic-section][data-path="${path}"]`)
}

async function tick(wrapper: ReturnType<typeof mountTab>, path: string) {
  const box = row(wrapper, path).get<HTMLInputElement>('[data-test=topic-pick]')
  box.element.checked = true
  await box.trigger('change')
}

/** A RouterLink that renders its target, so a spec can read where a link goes. */
const LinkStub = { props: ['to'], template: '<a :data-to="JSON.stringify(to)"><slot /></a>' }

describe('The Topics tab (admin design §5.5.2; Decisions "Topics editor")', () => {
  it('tells an editor of processing where the rules are edited, links each source and shows how to write one (#929)', async () => {
    setup(['rag-pipeline-editor'])
    vi.spyOn(api.collections, 'topics').mockResolvedValue({ collectionId: 'c1', topics: [] })
    const wrapper = mount(CollectionTopics, { props: { collectionId: 'c1', sources }, global: { stubs: { RouterLink: LinkStub } } })
    await flushPromises()

    const help = wrapper.get('[data-test=topics-help]')
    expect(help.text()).toContain(TOPICS.editRules)
    expect(help.findAll('[data-test=topics-source]').map((a) => [a.text(), JSON.parse(a.attributes('data-to')!)])).toEqual([
      ['umea.se', { name: 'source', params: { id: 's1' } }],
      ['Filer', { name: 'source', params: { id: 's2' } }],
    ])
    expect(help.text()).toContain(TOPICS.howTitle)
    // The example is valid rules JSON, as the Topics field takes it.
    expect(JSON.parse(TOPICS.example)).toEqual([
      { match: 'https://www.umea.se/bygga-bo-och-miljo/bygglov/', on: 'path', tags: ['bygglov'] },
      { match: '/kommun-och-politik/', on: 'path', tags: ['politik'] },
    ])
    expect(wrapper.text()).toContain(TOPICS.none)
  })

  it('lists one row per tag with its rules in words, their sources, and pending before the re-chunk', async () => {
    setup(['rag-pipeline-editor'])
    const wrapper = mountTab()
    await flushPromises()

    const rows = wrapper.get('[data-test=topics]').findAll('tbody tr')
    expect(rows.map((r) => r.get('.font-mono').text())).toEqual(['bygga', 'protokoll', 'kommun'])
    expect(rows[0]!.text()).toContain('under /bygga/')
    expect(rows[1]!.findAll('[data-test=topic-rule]').map((r) => r.text())).toEqual(['whose title contains “Protokoll”', 'PDF files'])
    expect(rows[1]!.text()).toContain('umea.se, Filer') // a tag set on two sources is one row naming both
    expect(rows[2]!.get('[data-test=topic-pending]').text()).toBe('pending')
    wrapper.unmount()
  })

  it('ticks a section with its proposed tag, keeps a covered one ticked, and checks the tag as typed', async () => {
    setup(['rag-pipeline-editor'])
    const wrapper = mountTab()
    await flushPromises()

    const blocks = wrapper.findAll('[data-test=topic-suggestions]')
    expect(blocks.map((b) => b.get('h3').text())).toEqual(['Suggested from Filer', 'Suggested from umea.se'])
    const covered = row(wrapper, '/bygga/').get<HTMLInputElement>('[data-test=topic-pick]')
    expect(covered.element.checked).toBe(true)
    expect(covered.element.disabled).toBe(true) // removed in the list, not here
    expect(row(wrapper, '/bygga/').get('[data-test=topic-covered]').text()).toBe('Already a topic: bygga')
    expect(wrapper.get('[data-test=add-topics]').attributes('aria-disabled')).toBe('true')
    expect(wrapper.get('[data-test=add-topics]').text()).toContain('Add topics') // no count until a section is ticked

    await tick(wrapper, '/utbildning/')
    const tag = row(wrapper, '/utbildning/').get<HTMLInputElement>('[data-test=topic-tag]')
    expect(tag.element.value).toBe('utbildning')
    expect(wrapper.get('[data-test=add-topics]').text()).toContain('Add 1 topic')
    expect(wrapper.get('[data-test=add-topics]').attributes('aria-disabled')).toBeUndefined()

    await tag.setValue('Utbildning och skola')
    expect(row(wrapper, '/utbildning/').get('[data-test=topic-tag-error]').text()).toBe('Use lower-case letters, digits and hyphens, at most 50.')
    expect(tag.attributes('aria-invalid')).toBe('true')
    expect(wrapper.get('[data-test=add-topics]').attributes('aria-disabled')).toBe('true')
    await tag.setValue('skola')
    expect(row(wrapper, '/utbildning/').find('[data-test=topic-tag-error]').exists()).toBe(false)

    // Unticking drops the pick.
    const box = row(wrapper, '/utbildning/').get<HTMLInputElement>('[data-test=topic-pick]')
    box.element.checked = false
    await box.trigger('change')
    expect(row(wrapper, '/utbildning/').find('[data-test=topic-tag]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows the sections under one section on asking', async () => {
    const suggestions = setup(['rag-pipeline-editor'])
    const wrapper = mountTab()
    await flushPromises()
    suggestions.mockResolvedValueOnce({ collectionId: 'c1', sources: [{ ...web, under: '/omsorg/', sections: [
      { path: '/omsorg/aldre/', documents: 120, title: 'Äldreomsorg', suggestedTag: 'aldre', coveredBy: [] },
    ] }] })

    const show = row(wrapper, '/omsorg/').get('[data-test=show-under]')
    expect(show.attributes('aria-expanded')).toBe('false')
    await show.trigger('click')
    await flushPromises()
    expect(suggestions).toHaveBeenLastCalledWith('c1', { sourceId: 's1', under: '/omsorg/' })
    expect(row(wrapper, '/omsorg/aldre/').text()).toContain('Äldreomsorg')
    expect(show.attributes('aria-expanded')).toBe('true')

    suggestions.mockResolvedValueOnce({ collectionId: 'c1', sources: [{ ...files, under: 'HR/', sections: [] }] })
    await row(wrapper, 'HR/').get('[data-test=show-under]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=none-under]').text()).toBe('No section under it has 5 documents or more.')
    wrapper.unmount()
  })

  it('saves two sections on one tag and a folder as two topics, one plan per processing, confirmed as one', async () => {
    setup(['rag-department-admin'])
    const definition = (pathRules: unknown[]) => ({ schema: 1, template: 'website', templateVersion: 1, params: { chunkTokens: 500, pathRules } })
    const pipelines: Record<string, Pipeline> = {
      p1: { id: 'p1', name: 'umea.se processing', templateId: 'website', departmentId: 'd1', publishedVersionId: 'v1', version: '4',
        versions: [{ id: 'v1', number: 1, state: 'published', definition: definition([{ match: '/bygga/', on: 'path', tags: ['bygga'] }]), publishedAt: null }] },
      p2: { id: 'p2', name: 'upload', templateId: 'upload', departmentId: null, publishedVersionId: 'v7', version: '9',
        versions: [{ id: 'v7', number: 3, state: 'published', definition: { schema: 1, template: 'upload', templateVersion: 1, params: {} }, publishedAt: null }] },
    }
    vi.spyOn(api.sources, 'get').mockImplementation(async (id: string) => ({ id, pipelineId: id === 's1' ? 'p1' : 'p2', collectionId: 'c1' }) as Source)
    vi.spyOn(api.pipelines, 'get').mockImplementation(async (id: string) => pipelines[id]!)
    const plan = vi.spyOn(api.pipelines, 'plan').mockImplementation(async (id: string) =>
      ({ publicationId: `pub-${id}`, pipelineId: id, draftVersionId: 'd', draftNumber: 2, copiedOnWrite: id === 'p2' }))
    const publication = (id: string, documents: number, sourceIds: string[]): Publication => ({
      id, state: 'succeeded', lastError: null, pipelineId: id, pipelineVersionId: 'v', draftNumber: 2, draftState: 'draft', version: `ver-${id}`,
      published: false, superseded: false, plan: { pipelineVersionId: 'v', documents, sources: sourceIds.map((sourceId) => ({ sourceId, documents })) },
    })
    vi.spyOn(api.publications, 'get').mockImplementation(async (id: string) =>
      id === 'pub-p1' ? publication(id, 2323, ['s1']) : publication(id, 9, ['s2', 'other']))
    const confirm = vi.spyOn(api.publications, 'confirm').mockImplementation(async (id: string) => publication(id, 0, []))
    const wrapper = mountTab()
    await flushPromises()

    await tick(wrapper, '/utbildning/')
    await tick(wrapper, '/omsorg/')
    await row(wrapper, '/omsorg/').get('[data-test=topic-tag]').setValue('utbildning')
    await tick(wrapper, 'HR/')
    expect(wrapper.get('[data-test=add-topics]').text()).toContain('Add 2 topics') // one topic, two rules; and hr
    await wrapper.get('[data-test=add-topics]').trigger('click')
    await flushPromises()

    expect(plan).toHaveBeenCalledTimes(2)
    // The published rules stay; the new ones are added. A platform processing is copied for the knowledge base (§6.6).
    expect(plan).toHaveBeenCalledWith('p1', { parameters: { chunkTokens: 500, pathRules: [
      { match: '/bygga/', on: 'path', tags: ['bygga'] },
      { match: '/utbildning/', on: 'path', tags: ['utbildning'] },
      { match: '/omsorg/', on: 'path', tags: ['utbildning'] },
    ] }, version: '4' })
    expect(plan).toHaveBeenCalledWith('p2', { parameters: { pathRules: [{ match: 'HR/', on: 'path', tags: ['hr'] }] }, version: '9', collectionId: 'c1' })

    const dialog = document.body.querySelector('[data-test=topics-plan]')!
    const items = [...dialog.querySelectorAll('[data-test=topics-plan-item]')].map((i) => i.textContent!.replace(/\s+/g, ' ').trim())
    expect(items[0]).toMatch(/^umea\.se processing: saving processes 2\s323 documents again/) // the plan's own count
    expect(items[0]).toContain('Sources: umea.se')
    expect(items[1]).toContain('Sources: Filer, 1 other source')
    expect(confirm).not.toHaveBeenCalled()

    document.body.querySelector<HTMLButtonElement>('[data-test=confirm]')!.click()
    await flushPromises()
    expect(confirm).toHaveBeenCalledWith('pub-p1', 'ver-pub-p1')
    expect(confirm).toHaveBeenCalledWith('pub-p2', 'ver-pub-p2')
    expect(wrapper.findAll('[data-test=topic-tag]')).toHaveLength(0) // the picks are cleared, the list read again
    wrapper.unmount()
  })

  it('says when a plan fails and confirms nothing', async () => {
    setup(['rag-pipeline-editor'])
    vi.spyOn(api.sources, 'get').mockResolvedValue({ id: 's1', pipelineId: 'p1', collectionId: 'c1' } as Source)
    vi.spyOn(api.pipelines, 'get').mockResolvedValue({ id: 'p1', name: 'umea.se processing', templateId: 'website', departmentId: 'd1', publishedVersionId: null, version: '1', versions: [] })
    vi.spyOn(api.pipelines, 'plan').mockResolvedValue({ publicationId: 'pub', pipelineId: 'p1', draftVersionId: 'd', draftNumber: 1, copiedOnWrite: false })
    vi.spyOn(api.publications, 'get').mockResolvedValue({
      id: 'pub', state: 'failed', plan: null, lastError: 'boom', pipelineId: 'p1', pipelineVersionId: 'v', draftNumber: 1, draftState: 'draft', version: '1', published: false, superseded: false,
    })
    const confirm = vi.spyOn(api.publications, 'confirm')
    const wrapper = mountTab()
    await flushPromises()
    await tick(wrapper, '/utbildning/')
    await wrapper.get('[data-test=add-topics]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=topics-save-error]').text()).toContain('The plan for umea.se processing could not be made. Nothing was saved.')
    expect(document.body.querySelector('[data-test=topics-plan]')).toBeNull()
    expect(confirm).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('is read-only for Edit and Read of the owner, and tags and counts only for a shared department', async () => {
    const suggestions = setup(['rag-editor'])
    const owner = mountTab()
    await flushPromises()
    expect(owner.get('[data-test=topics-read-only]').text()).toBe('Topics are changed by whoever may edit processing.')
    expect(owner.find('[data-test=topic-suggestions]').exists()).toBe(false)
    expect(owner.find('[data-test=topics-help]').exists()).toBe(false)
    expect(owner.get('[data-test=topics]').text()).toContain('under /bygga/')
    expect(suggestions).not.toHaveBeenCalled()
    owner.unmount()

    setup(['rag-department-admin'])
    vi.spyOn(api.collections, 'topics').mockResolvedValue({ collectionId: 'c1', topics: [{ tag: 'bygga', documents: 412, rules: null, pending: false }] })
    const shared = mountTab({ collectionId: 'c1' })
    await flushPromises()
    expect(shared.get('[data-test=topics]').text()).not.toContain('Rule')
    expect(shared.get('[data-test=topics]').text()).not.toContain('Source')
    expect(shared.find('[data-test=topics-read-only]').exists()).toBe(false)
    expect(shared.find('[data-test=topic-suggestions]').exists()).toBe(false)
    expect(shared.find('[data-test=topics-help]').exists()).toBe(false) // no help for rules it cannot edit
    shared.unmount()
  })
})
