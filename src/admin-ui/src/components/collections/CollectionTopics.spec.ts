import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Pipeline, Publication, Source, SourceTopicSuggestions, Topic } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { openMenu } from '@/test/helpers'
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
    { path: '/bygga/', documents: 612, title: 'Bygga, bo och miljö', suggestedTag: 'bygga', coveredBy: ['bygga'], hosts: ['www.umea.se'] },
    { path: '/utbildning/', documents: 488, title: 'Utbildning och barnomsorg', suggestedTag: 'utbildning', coveredBy: [], hosts: ['www.umea.se'] },
    { path: '/omsorg/', documents: 401, title: null, suggestedTag: 'omsorg', coveredBy: [], hosts: ['www.umea.se'] },
  ],
}
const files: SourceTopicSuggestions = {
  sourceId: 's2', sourceName: 'Filer', kind: 'upload', under: null, sections: [{ path: 'HR/', documents: 9, title: null, suggestedTag: 'hr', coveredBy: [], hosts: [] }],
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

describe('The Topics tab (admin design §5.5.2; Decisions "Topics editor")', () => {
  it('lists one row per tag with its rules in words, their sources, and pending before the re-chunk', async () => {
    setup(['rag-pipeline-editor'])
    const wrapper = mountTab()
    await flushPromises()

    const rows = wrapper.get('[data-test=topics]').findAll('tbody tr')
    expect(rows.map((r) => r.get('.font-mono').text())).toEqual(['bygga', 'protokoll', 'kommun'])
    expect(rows[0]!.text()).toContain('under /bygga/')
    expect(rows[1]!.findAll('[data-test=topic-rule-words]').map((r) => r.text())).toEqual(['whose title contains “Protokoll”', 'PDF files'])
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
      { path: '/omsorg/aldre/', documents: 120, title: 'Äldreomsorg', suggestedTag: 'aldre', coveredBy: [], hosts: ['www.umea.se'] },
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
      { match: 'www.umea.se/utbildning/', on: 'path', tags: ['utbildning'] },
      { match: 'www.umea.se/omsorg/', on: 'path', tags: ['utbildning'] },
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

  it('edits one rule of a topic from its stored form and saves it through the plan', async () => {
    setup(['rag-pipeline-editor'])
    const stored = [{ match: '/bygga/', on: 'path', tags: ['bygga'] }, { match: 'Protokoll', on: 'title-regex', tags: ['protokoll'] }]
    vi.spyOn(api.sources, 'get').mockResolvedValue({ id: 's1', pipelineId: 'p1', collectionId: 'c1' } as Source)
    vi.spyOn(api.pipelines, 'get').mockResolvedValue({
      id: 'p1', name: 'umea.se processing', templateId: 'website', departmentId: 'd1', publishedVersionId: 'v1', version: '4',
      versions: [{ id: 'v1', number: 1, state: 'published', definition: { schema: 1, template: 'website', templateVersion: 1, params: { chunkTokens: 500, pathRules: stored } }, publishedAt: null }],
    })
    vi.spyOn(api.collections, 'previewTopicRule').mockResolvedValue({ matched: 12, total: 2323, checked: 2323, partial: false, sample: [] })
    const plan = vi.spyOn(api.pipelines, 'plan').mockResolvedValue({ publicationId: 'pub', pipelineId: 'p1', draftVersionId: 'd', draftNumber: 2, copiedOnWrite: false })
    vi.spyOn(api.publications, 'get').mockResolvedValue({
      id: 'pub', state: 'succeeded', lastError: null, pipelineId: 'p1', pipelineVersionId: 'v', draftNumber: 2, draftState: 'draft', version: 'ver',
      published: false, superseded: false, plan: { pipelineVersionId: 'v', documents: 2323, sources: [{ sourceId: 's1', documents: 2323 }] },
    })
    const confirm = vi.spyOn(api.publications, 'confirm').mockResolvedValue({} as Publication)
    const wrapper = mountTab()
    await flushPromises()

    const protokoll = wrapper.get('[data-test=topics]').findAll('tbody tr')[1]!
    await protokoll.findAll('[data-test=edit-rule]')[0]!.trigger('click')
    await flushPromises()
    const title = document.body.querySelector<HTMLInputElement>('#topic-title')!
    expect(title.value).toBe('Protokoll') // the stored rule, shown in words
    title.value = 'Protokoll (KF)'
    title.dispatchEvent(new Event('input'))
    await flushPromises()
    document.body.querySelector<HTMLFormElement>('[data-test=topic-form]')!.dispatchEvent(new Event('submit', { cancelable: true }))
    await flushPromises()

    expect(plan).toHaveBeenCalledWith('p1', { parameters: { chunkTokens: 500, pathRules: [
      stored[0], { match: '(?i)Protokoll \\(KF\\)', on: 'title-regex', tags: ['protokoll'] },
    ] }, version: '4' })
    expect(document.body.textContent).toContain('Save the topic protokoll?')
    document.body.querySelector<HTMLButtonElement>('[data-test=confirm]')!.click()
    await flushPromises()
    expect(confirm).toHaveBeenCalledWith('pub', 'ver')
    wrapper.unmount()
  })

  it('removes a topic from every source that sets it, in one confirm', async () => {
    setup(['rag-pipeline-editor'])
    const pipelines: Record<string, Pipeline> = {
      p1: { id: 'p1', name: 'umea.se processing', templateId: 'website', departmentId: 'd1', publishedVersionId: 'v1', version: '4', versions: [
        { id: 'v1', number: 1, state: 'published', publishedAt: null, definition: { schema: 1, template: 'website', templateVersion: 1, params: { pathRules: [
          { match: '/bygga/', on: 'path', tags: ['bygga'] }, { match: 'Protokoll', on: 'title-regex', tags: ['protokoll'] },
        ] } } },
      ] },
      p2: { id: 'p2', name: 'files', templateId: 'files', departmentId: 'd1', publishedVersionId: 'v2', version: '7', versions: [
        { id: 'v2', number: 1, state: 'published', publishedAt: null, definition: { schema: 1, template: 'files', templateVersion: 1, params: { pathRules: [
          { match: 'application/pdf', on: 'mediaType', tags: ['protokoll', 'pdf'] },
        ] } } },
      ] },
    }
    vi.spyOn(api.sources, 'get').mockImplementation(async (id: string) => ({ id, pipelineId: id === 's1' ? 'p1' : 'p2', collectionId: 'c1' }) as Source)
    vi.spyOn(api.pipelines, 'get').mockImplementation(async (id: string) => pipelines[id]!)
    const plan = vi.spyOn(api.pipelines, 'plan').mockImplementation(async (id: string) => ({ publicationId: `pub-${id}`, pipelineId: id, draftVersionId: 'd', draftNumber: 2, copiedOnWrite: false }))
    vi.spyOn(api.publications, 'get').mockImplementation(async (id: string) => ({
      id, state: 'succeeded', lastError: null, pipelineId: id, pipelineVersionId: 'v', draftNumber: 2, draftState: 'draft', version: `ver-${id}`,
      published: false, superseded: false, plan: { pipelineVersionId: 'v', documents: 10, sources: [] },
    }))
    const confirm = vi.spyOn(api.publications, 'confirm').mockResolvedValue({} as Publication)
    const wrapper = mountTab()
    await flushPromises()

    await wrapper.get('[data-test=topic-menu-protokoll] button').trigger('click')
    await flushPromises()
    await openMenu().get('[role=menuitem]').trigger('click')
    await flushPromises()

    expect(plan).toHaveBeenCalledWith('p1', { parameters: { pathRules: [{ match: '/bygga/', on: 'path', tags: ['bygga'] }] }, version: '4' })
    // A rule that sets another topic too keeps it.
    expect(plan).toHaveBeenCalledWith('p2', { parameters: { pathRules: [{ match: 'application/pdf', on: 'mediaType', tags: ['pdf'] }] }, version: '7' })
    expect(document.body.textContent).toContain('Remove the topic protokoll?')
    expect(document.body.textContent).toContain('The processing is published without the topic’s rules')
    document.body.querySelector<HTMLButtonElement>('[data-test=confirm]')!.click()
    await flushPromises()
    expect(confirm).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('is read-only for Edit and Read of the owner, and tags and counts only for a shared department', async () => {
    const suggestions = setup(['rag-editor'])
    const owner = mountTab()
    await flushPromises()
    expect(owner.get('[data-test=topics-read-only]').text()).toBe('Topics are changed by whoever may edit processing.')
    expect(owner.find('[data-test=topic-suggestions]').exists()).toBe(false)
    expect(owner.find('[data-test=add-topic]').exists()).toBe(false)
    expect(owner.find('[data-test=edit-rule]').exists()).toBe(false)
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
    expect(shared.find('[data-test=add-topic]').exists()).toBe(false)
    shared.unmount()
  })
})

describe('The Topics tab after review (#1075, #1076)', () => {
  const definition = (pathRules: unknown[]) => ({ schema: 1, template: 'website', templateVersion: 1, params: { pathRules } })
  const publication = (id: string): Publication => ({
    id, state: 'succeeded', lastError: null, pipelineId: id, pipelineVersionId: 'v', draftNumber: 2, draftState: 'draft', version: `ver-${id}`,
    published: false, superseded: false, plan: { pipelineVersionId: 'v', documents: 5, sources: [{ sourceId: 's1', documents: 5 }] },
  })
  /** Two processings, s1 on p1 and s2 on p2; p1 has a draft someone left. */
  function plans() {
    const pipelines: Record<string, Pipeline> = {
      p1: { id: 'p1', name: 'web', templateId: 'website', departmentId: 'd1', publishedVersionId: 'v1', version: '4', versions: [
        { id: 'v1', number: 1, state: 'published', definition: definition([]), publishedAt: null },
        // A source page's unpublished edit: more than the topics changes.
        { id: 'v2', number: 2, state: 'draft', definition: { ...definition([]), params: { pathRules: [], chunkTokens: 300 } }, publishedAt: null },
      ] },
      p2: { id: 'p2', name: 'files', templateId: 'upload', departmentId: 'd1', publishedVersionId: 'v7', version: '9', versions: [
        { id: 'v7', number: 1, state: 'published', definition: definition([]), publishedAt: null },
        // A cancelled topic plan's own draft: only the topics differ, so no warning.
        { id: 'v8', number: 2, state: 'draft', definition: definition([{ match: 'HR/', on: 'path', tags: ['hr'] }]), publishedAt: null },
      ] },
    }
    vi.spyOn(api.sources, 'get').mockImplementation(async (id: string) => ({ id, pipelineId: id === 's1' ? 'p1' : 'p2', collectionId: 'c1' }) as Source)
    vi.spyOn(api.pipelines, 'get').mockImplementation(async (id: string) => pipelines[id]!)
    const plan = vi.spyOn(api.pipelines, 'plan').mockImplementation(async (id: string) =>
      ({ publicationId: `pub-${id}`, pipelineId: id, draftVersionId: 'd', draftNumber: 2, copiedOnWrite: false }))
    vi.spyOn(api.publications, 'get').mockImplementation(async (id: string) => publication(id))
    return plan
  }

  it('retries only the plans a failed Confirm left, reads the list after each attempt, and says when a draft is replaced', async () => {
    setup(['rag-pipeline-editor'])
    plans()
    const topicsCall = vi.mocked(api.collections.topics)
    const confirm = vi.spyOn(api.publications, 'confirm')
      .mockResolvedValueOnce(publication('pub-p1'))
      .mockRejectedValueOnce(new Error('stale'))
      .mockResolvedValue(publication('pub-p2'))
    const wrapper = mountTab()
    await flushPromises()
    await tick(wrapper, '/utbildning/')
    await tick(wrapper, 'HR/')
    await wrapper.get('[data-test=add-topics]').trigger('click')
    await flushPromises()
    expect([...document.body.querySelectorAll('[data-test=topics-plan-draft]')]).toHaveLength(1) // p1's settings draft only

    const reads = topicsCall.mock.calls.length
    document.body.querySelector<HTMLButtonElement>('[data-test=confirm]')!.click()
    await flushPromises()
    expect(confirm.mock.calls.map((c) => c[0])).toEqual(['pub-p1', 'pub-p2'])
    expect(topicsCall.mock.calls.length).toBe(reads + 1) // read again after the failure
    expect(document.body.querySelector('[data-test=topics-plan]')).not.toBeNull() // the dialog stays, with the error
    document.body.querySelector<HTMLButtonElement>('[data-test=confirm]')!.click()
    await flushPromises()
    expect(confirm.mock.calls.map((c) => c[0])).toEqual(['pub-p1', 'pub-p2', 'pub-p2'])
    expect(wrapper.findAll('[data-test=topic-tag]')).toHaveLength(0)
    wrapper.unmount()
  })

  it('starts no second change while one is planned: Add topic, Edit, the row menu and Add N are disabled', async () => {
    setup(['rag-pipeline-editor'])
    plans()
    vi.spyOn(api.publications, 'get').mockImplementation(() => new Promise(() => undefined)) // the plan never finishes
    const wrapper = mountTab()
    await flushPromises()
    await tick(wrapper, '/utbildning/')
    await wrapper.get('[data-test=add-topics]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-test=add-topic]').attributes('aria-disabled') ?? wrapper.get('[data-test=add-topic]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-test=edit-rule]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-test=topics-planning]').text()).toBe('Working out what needs processing again…')
    // The row menu's Remove is disabled, and choosing it starts nothing (the review of #1080).
    const plan = vi.mocked(api.pipelines.plan)
    const calls = plan.mock.calls.length
    await wrapper.get('[data-test=topic-menu-protokoll] button').trigger('click')
    await flushPromises()
    const item = openMenu().get('[role=menuitem]')
    expect(item.attributes('aria-disabled')).toBe('true')
    await item.trigger('click')
    await flushPromises()
    expect(plan.mock.calls.length).toBe(calls)
    wrapper.unmount()
  })

  it('closes the open levels when the suggestions are read again, and drops the picks under a level it closes', async () => {
    const suggestions = setup(['rag-pipeline-editor'])
    const wrapper = mountTab()
    await flushPromises()
    suggestions.mockResolvedValueOnce({ collectionId: 'c1', sources: [{ ...web, under: '/omsorg/', sections: [
      { path: '/omsorg/aldre/', documents: 120, title: 'Äldreomsorg', suggestedTag: 'aldre', coveredBy: [], hosts: ['www.umea.se'] },
    ] }] })
    await row(wrapper, '/omsorg/').get('[data-test=show-under]').trigger('click')
    await flushPromises()
    expect(row(wrapper, '/omsorg/aldre/').get('[data-test=topic-pick]').attributes('aria-label')).toBe('Add Äldreomsorg, under /omsorg/, as a topic')
    await tick(wrapper, '/omsorg/aldre/')
    expect(wrapper.get('[data-test=add-topics]').text()).toContain('Add 1 topic')
    await row(wrapper, '/omsorg/').get('[data-test=show-under]').trigger('click')
    expect(wrapper.get('[data-test=add-topics]').text()).toContain('Add topics') // nothing picked that doesn't show

    await row(wrapper, '/omsorg/').get('[data-test=show-under]').trigger('click') // open again (no new request)
    expect(wrapper.find('[data-test=topic-section][data-path="/omsorg/aldre/"]').exists()).toBe(true)
    suggestions.mockResolvedValue({ collectionId: 'c1', sources: [{ ...files }, { ...web }] }) // a new answer, as after a save
    ;(wrapper.vm as unknown as { $: { setupState: { load: () => void } } }).$.setupState.load()
    await flushPromises()
    expect(wrapper.find('[data-test=topic-section][data-path="/omsorg/aldre/"]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('Host-first rules and Pending removal (Decisions "Topics editor", #1082)', () => {
  it('writes one host-first rule per host a website section was found on, and a folder as it is', async () => {
    const suggestions = setup(['rag-pipeline-editor'])
    suggestions.mockResolvedValue({ collectionId: 'c1', sources: [files, { ...web, sections: [
      { path: '/omsorg/', documents: 401, title: null, suggestedTag: 'omsorg', coveredBy: [], hosts: ['umea.se', 'www.umea.se'] },
    ] }] })
    const definition = { schema: 1, template: 'website', templateVersion: 1, params: { pathRules: [] } }
    vi.spyOn(api.sources, 'get').mockImplementation(async (id: string) => ({ id, pipelineId: id === 's1' ? 'p1' : 'p2', collectionId: 'c1' }) as Source)
    vi.spyOn(api.pipelines, 'get').mockImplementation(async (id: string) => ({
      id, name: id, templateId: 'website', departmentId: 'd1', publishedVersionId: 'v1', version: '1',
      versions: [{ id: 'v1', number: 1, state: 'published', definition, publishedAt: null }],
    }))
    const plan = vi.spyOn(api.pipelines, 'plan').mockImplementation(async (id: string) => ({ publicationId: `pub-${id}`, pipelineId: id, draftVersionId: 'd', draftNumber: 2, copiedOnWrite: false }))
    vi.spyOn(api.publications, 'get').mockImplementation(() => new Promise(() => undefined))
    const wrapper = mountTab()
    await flushPromises()
    await tick(wrapper, '/omsorg/')
    await tick(wrapper, 'HR/')
    await wrapper.get('[data-test=add-topics]').trigger('click')
    await flushPromises()
    expect(plan).toHaveBeenCalledWith('p1', { parameters: { pathRules: [
      { match: 'umea.se/omsorg/', on: 'path', tags: ['omsorg'] }, { match: 'www.umea.se/omsorg/', on: 'path', tags: ['omsorg'] },
    ] }, version: '1' })
    expect(plan).toHaveBeenCalledWith('p2', { parameters: { pathRules: [{ match: 'HR/', on: 'path', tags: ['hr'] }] }, version: '1' })
    wrapper.unmount()
  })

  it('shows a removed topic as Pending removal with its count and no menu until its re-chunk is done', async () => {
    setup(['rag-pipeline-editor'])
    vi.mocked(api.collections.topics).mockResolvedValue({ collectionId: 'c1', topics: [
      ...topics, { tag: 'skola', documents: 4, rules: [], pending: false, pendingRemoval: true },
    ] })
    const wrapper = mountTab()
    await flushPromises()
    const row = wrapper.findAll('[data-test=topics] tbody tr').find((r) => r.text().includes('skola'))!
    expect(row.get('[data-test=topic-pending-removal]').text()).toBe('4 · Pending removal')
    expect(row.find('[data-test=topic-menu-skola]').exists()).toBe(false)
    wrapper.unmount()
  })
})
