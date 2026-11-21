import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { Collection, DocumentSummary, Source, SourceListItem } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser, collection, collectionSummary, collectionUser } from '@/test/fixtures'
import { button } from '@/test/helpers'
import CollectionEditDrawer from '@/components/collections/CollectionEditDrawer.vue'
import CollectionView from './CollectionView.vue'

// `?tab=` (the Files tab's `?tab=files`, §5.5.7) through a route the tests set.
const route = vi.hoisted(() => ({ query: {} as Record<string, string> }))
const push = vi.hoisted(() => vi.fn())
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRoute: () => route, useRouter: () => ({ push }) }))
beforeEach(() => {
  route.query = {}
})

const base: Collection = collection({ usedBy: [collectionUser({ published: false, draft: true })], version: '7' })

function document(overrides: Partial<DocumentSummary>): DocumentSummary {
  return {
    id: 'doc', sourceId: 's1', title: 'Altan', url: 'https://www.umea.se/altan', displayName: null, mediaType: 'text/html', parseOutcome: 'ok',
    piiFlagged: false, piiCleared: false, held: false, live: true, sourceModifiedAt: null, fetchedAt: '2026-11-01T02:14:00Z', lastSeenAt: '2026-11-01T02:14:00Z',
    ...overrides,
  }
}

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}

describe('Knowledge base (admin design §5.5.2)', () => {
  it('shows the state, who uses it, its sources, and documents filtered to personal data', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([
      { id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'internal', state: 'active', documents: 0, lastRun: null },
    ])
    const list = vi.spyOn(api.documents, 'list').mockImplementation(async (q) => ({
      items: q.pii === 'flagged' ? [document({ id: 'p', title: 'Protokoll', piiFlagged: true })] : [document({}), document({ id: 'p', title: 'Protokoll', piiFlagged: true })],
      next: null,
    }))

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=state]').text()).toBe('Not published')
    expect(wrapper.text()).toContain('Not published: only visible in Test.')
    expect(wrapper.get('[data-test=used-by]').text()).toBe('Bygglovsassistenten (draft)')
    expect(wrapper.find('[data-test=publish]').exists()).toBe(false) // publishing is Manage

    await button(wrapper, 'Sources (1)').trigger('click')
    expect(wrapper.text()).toContain('umea.se/bygga')
    expect(wrapper.text()).toContain('Website')

    await button(wrapper, 'Documents').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('[data-test=pii]')).toHaveLength(1)
    expect(wrapper.text()).toContain('Altan')
    await wrapper.get('input[type=checkbox]').setValue(true)
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith(expect.objectContaining({ collectionId: 'c1', pii: 'flagged' }))
    expect(wrapper.text()).not.toContain('Altan')
  })

  it('loads the documents when opened on the Documents tab (a link or a reload)', async () => {
    signIn(['rag-editor'])
    route.query = { tab: 'documents' }
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({ hasSet: false, questions: [], latestRuns: [] })
    vi.spyOn(api.collections, 'setup').mockResolvedValue(null as never)
    const list = vi.spyOn(api.documents, 'list').mockResolvedValue({ items: [document({})], next: null })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()

    expect(list).toHaveBeenCalledWith(expect.objectContaining({ collectionId: 'c1' }))
    expect(wrapper.text()).toContain('Altan')
    expect(wrapper.text()).not.toContain('No documents yet')
  })

  it('shows the document count, the last fetch with its result and the latest quality on the overview', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([
      {
        id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'internal', state: 'active', documents: 2300,
        lastRun: { id: 'r1', state: 'succeeded', startedAt: '2026-10-30T01:00:00Z', finishedAt: '2026-10-30T01:12:00Z' },
      },
      {
        id: 's2', collectionId: 'c1', type: 'upload', name: 'Blanketter', classification: 'internal', state: 'active', documents: 24,
        lastRun: { id: 'r2', state: 'failed', startedAt: '2026-10-31T09:00:00Z', finishedAt: '2026-10-31T09:01:00Z' },
      },
    ])
    const run = (id: string, variant: string, recallAtK: number, candidate = false) => ({
      id, kind: 'nightly', variant, questionCount: 40, k: 10, found: 36, recallAtK, mrr: 0.8, wins: null, losses: null, comparedRunId: null, candidate,
      thresholdProposal: null, startedAt: '2026-11-01T02:00:00Z', finishedAt: '2026-11-01T02:05:00Z', documentGone: null,
    })
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({
      hasSet: true, questions: [], latestRuns: [run('e3', 'hybrid', 0.95, true), run('e2', 'vector', 0.8), run('e1', 'hybrid', 0.91)],
    })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=document-count]').text().replace(/\s/g, '')).toBe('0') // the detail's live count, not the sources' sum
    expect(wrapper.get('[data-test=last-fetch]').text()).toContain('Failed')
    expect(wrapper.text()).toContain('Latest quality (recall@10)')
    expect(wrapper.get('[data-test=quality]').text()).toContain('0,91')
  })

  it('lists each source with its last run and document count on the Sources tab (§5.5.2)', async () => {
    signIn(['rag-viewer'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([
      {
        id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'internal', state: 'active', documents: 2300,
        lastRun: { id: 'r1', state: 'failed', startedAt: '2026-10-30T01:00:00Z', finishedAt: '2026-10-30T01:12:00Z' },
      },
      { id: 's2', collectionId: 'c1', type: 'upload', name: 'Blanketter', classification: 'internal', state: 'active', documents: 0, lastRun: null },
    ])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({ hasSet: false, questions: [], latestRuns: [] })

    const wrapper = mount(CollectionView, { props: { id: 'c1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    await button(wrapper, 'Sources (2)').trigger('click')

    const headers = wrapper.findAll('th').map((h) => h.text())
    expect(headers).toEqual(expect.arrayContaining(['Last run', 'Documents']))
    const [crawl, upload] = wrapper.findAll('tbody tr').map((r) => r.text())
    expect(crawl).toContain('Failed')
    expect(crawl?.replace(/\s/g, '')).toContain('2300')
    expect(upload).toContain('Not run yet')
    // A source row is a link to the source (§5.5.2 "Lists open as links").
    expect(wrapper.findAll('tbody tr').map((r) => r.getComponent(RouterLinkStub).props('to'))).toEqual([
      { name: 'source', params: { id: 's1' } }, { name: 'source', params: { id: 's2' } },
    ])
  })

  it('shows the live document count of the list, not every document that is not deleted (#374)', async () => {
    signIn(['rag-viewer'])
    // The review's case: 1 166 live documents of 1 270; the sources still say 1 270 if they count differently.
    vi.spyOn(api.collections, 'get').mockResolvedValue({ ...base, documentCount: 1166 })
    vi.spyOn(api.collections, 'sources').mockResolvedValue([
      { id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se', classification: 'internal', state: 'active', documents: 1270, lastRun: null },
    ])

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=document-count]').text()).toMatch(/^1\s166$/)
  })

  it('marks an archived assistant in Used by on the overview and the Sharing tab (#374)', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(collection({
      usedBy: [
        collectionUser({ id: 'a1', name: 'Permits assistant' }),
        collectionUser({ id: 'a2', name: 'GK check', state: 'archived', published: false, draft: true }),
      ],
    }))
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    vi.spyOn(api.organisation, 'departmentNames').mockResolvedValue([{ id: 'd1', name: 'Kommunikation' }])

    const wrapper = mount(CollectionView, { props: { id: 'c1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    expect(wrapper.get('[data-test=used-by]').text()).toBe('Permits assistant, GK check (archived)')
    // Each name links to its assistant (§5.5.2), the archived one too.
    expect(wrapper.findAllComponents(RouterLinkStub).map((l) => l.props('to'))).toEqual([
      { name: 'assistant', params: { id: 'a1' } },
      { name: 'assistant', params: { id: 'a2' } },
    ])

    await button(wrapper, 'Sharing').trigger('click')
    await flushPromises()
    const rows = wrapper.get('[data-test=sharing-used-by]').findAll('tbody tr').map((r) => r.text())
    expect(rows).toHaveLength(2)
    expect(rows[0]).toContain('Published')
    expect(rows[1]).toContain('GK check')
    expect(rows[1]).toContain('Archived')
    expect(rows[1]).not.toContain('Draft')
  })

  it('deletes an unused knowledge base after its name is typed (Decisions "Deleting knowledge bases, sources and assistants")', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(collection({ usedBy: [], documentCount: 0, version: '7' }))
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    const remove = vi.spyOn(api.collections, 'remove').mockResolvedValue(undefined)
    push.mockClear()

    const wrapper = mount(CollectionView, { props: { id: 'c1' }, attachTo: window.document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    await wrapper.get('[data-test=delete]').trigger('click')
    await flushPromises()
    expect(window.document.body.textContent).toContain('This cannot be undone. Its name is free for a new knowledge base.')
    const confirm = window.document.body.querySelector<HTMLButtonElement>('[data-test=confirm]')!
    expect(confirm.getAttribute('aria-disabled')).toBe('true')
    const name = window.document.body.querySelector<HTMLInputElement>('[data-test=confirm-name] input, input[data-test=confirm-name]')!
    name.value = base.name
    name.dispatchEvent(new Event('input'))
    await flushPromises()
    confirm.click()
    await flushPromises()
    expect(remove).toHaveBeenCalledWith('c1', '7')
    expect(push).toHaveBeenCalledWith({ name: 'collections' })
    wrapper.unmount()
  })

  it('names the assistants and sources that keep a knowledge base in use, with no Delete button; an editor has no Delete', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(collection({
      usedBy: [collectionUser({ id: 'a2', name: 'GK check', state: 'archived', published: false, draft: true })],
    }))
    vi.spyOn(api.collections, 'sources').mockResolvedValue([
      { id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se/bygga', classification: 'internal', state: 'active', documents: 3, lastRun: null },
    ])
    const remove = vi.spyOn(api.collections, 'remove').mockResolvedValue(undefined)

    const wrapper = mount(CollectionView, { props: { id: 'c1' }, attachTo: window.document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    await wrapper.get('[data-test=delete]').trigger('click')
    await flushPromises()
    expect(window.document.body.querySelector('[data-test=in-use-assistants]')!.textContent).toBe('Used by: GK check (archived). Delete or unlink them first.')
    expect(window.document.body.querySelector('[data-test=in-use-sources]')!.textContent).toBe('It has 1 source. Delete its sources first.')
    expect(window.document.body.querySelector('[data-test=confirm]')).toBeNull()
    expect(remove).not.toHaveBeenCalled()
    wrapper.unmount()

    signIn(['rag-editor'])
    const editor = mount(CollectionView, { props: { id: 'c1' }, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    expect(editor.find('[data-test=delete]').exists()).toBe(false)
  })

  it('offers Add contact on an empty contact, which opens the Edit drawer; a reader sees no button (§5.5.2)', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(collection())
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    expect(wrapper.text()).toContain('No contact yet')
    expect(wrapper.getComponent(CollectionEditDrawer).props('open')).toBe(false)
    await wrapper.get('[data-test=add-contact]').trigger('click')
    expect(wrapper.getComponent(CollectionEditDrawer).props('open')).toBe(true)
    wrapper.unmount()

    signIn(['rag-viewer'])
    const reader = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    expect(reader.text()).toContain('No contact yet')
    expect(reader.find('[data-test=add-contact]').exists()).toBe(false)
  })

  it('shows a set contact without Add contact', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(collection({ contactName: 'Anna', contactEmail: 'anna@umea.se' }))
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    expect(wrapper.get('[data-test=contact]').text()).toBe('Anna, anna@umea.se')
    expect(wrapper.find('[data-test=add-contact]').exists()).toBe(false)
  })

  it('lets a department admin publish with the version it read', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    const publish = vi.spyOn(api.collections, 'publish').mockResolvedValue({ ...base, publishedAt: '2026-11-01T11:00:00Z', version: '8' })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    await wrapper.get('[data-test=publish]').trigger('click')
    await flushPromises()

    expect(publish).toHaveBeenCalledWith('c1', '7')
    expect(wrapper.get('[data-test=state]').text()).toBe('Published')
  })

  it('takes the evaluation report threshold proposal to Search settings, filled in', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    vi.spyOn(api.collections, 'evaluation').mockResolvedValue({
      hasSet: true,
      questions: [{ layer: 'human', state: 'active', count: 2 }],
      latestRuns: [{
        id: 'r1', kind: 'nightly', variant: 'hybrid', questionCount: 2, k: 10, found: 1, recallAtK: 0.5, mrr: 0.5, wins: null, losses: null, comparedRunId: null,
        candidate: false, thresholdProposal: { answerable: 1, unanswerable: 1, coverageMaxDistance: { value: 0.4, misclassified: 0 } },
        startedAt: '2026-11-01T02:00:00Z', finishedAt: '2026-11-01T02:01:00Z', documentGone: null,
      }],
    })
    vi.spyOn(api.evaluation, 'results').mockResolvedValue([])
    vi.spyOn(api.evaluation, 'questions').mockResolvedValue({ items: [], next: null })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    await button(wrapper, 'Test questions and quality').trigger('click')
    await flushPromises()
    await wrapper.get('[data-test=use-proposal]').trigger('click')
    await flushPromises()

    expect(wrapper.find('[data-test=retrieval]').exists()).toBe(true)
    expect((wrapper.get('#rs-coverageMaxDistance').element as HTMLInputElement).value).toBe('0,4')
  })

  it('shows Get started on Overview until the checklist is done', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    const setup = vi.spyOn(api.collections, 'setup').mockResolvedValue({
      collectionId: 'c1', name: 'Bygglov', sources: 0, firstSyncDone: false, syncRunning: false, testQuestions: 0, evaluated: false, evaluating: false,
      published: false, assistantId: null, assistantPublished: false, available: false, done: false,
    })
    const stubs = { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } }
    const wrapper = mount(CollectionView, { props: { id: 'c1' }, ...stubs })
    await flushPromises()
    expect(wrapper.get('[data-test=setup]').text()).toContain('Add source')
    expect(wrapper.get('[data-test=step-source]').attributes('data-state')).toBe('todo')

    setup.mockResolvedValue({ ...(await setup.mock.results[0]!.value), done: true, available: true })
    const done = mount(CollectionView, { props: { id: 'c1' }, ...stubs })
    await flushPromises()
    expect(done.find('[data-test=setup]').exists()).toBe(false)
  })

  it('opens a knowledge base shared with the department read-only: Overview and Topics, nothing else', async () => {
    signIn(['rag-department-admin'])
    vi.spyOn(api.collections, 'get').mockResolvedValue({
      ...collectionSummary({
        id: 'c9', name: 'umea.se', departmentId: 'd9', departmentName: 'Kommunikation', classification: 'public', publishedAt: '2026-11-01T10:00:00Z',
        sharedWith: 'municipality', contactName: 'K. Lund', contactEmail: 'kommunikation@umea.se', documentCount: 2323, topics: ['bygglov'],
      }),
      shared: true,
    })
    const sources = vi.spyOn(api.collections, 'sources')
    const topics = vi.spyOn(api.collections, 'topics').mockResolvedValue({ collectionId: 'c9', topics: [{ tag: 'bygglov', documents: 412, rules: null }] })

    const wrapper = mount(CollectionView, { props: { id: 'c9' } })
    await flushPromises()

    expect(wrapper.findAll('[role=tab]').map((t) => t.text())).toEqual(['Overview', 'Topics'])
    expect(wrapper.get('[data-test=shared-with-you]').text()).toContain('Kommunikation shares this knowledge base with your department.')
    expect(wrapper.get('[data-test=contact]').text()).toBe('K. Lund, kommunikation@umea.se')
    expect(wrapper.get('[data-test=document-count]').text()).toMatch(/^2\s323$/)
    expect(wrapper.find('[data-test=publish]').exists()).toBe(false)
    expect(sources).not.toHaveBeenCalled() // its sources are not ours to see

    await button(wrapper, 'Topics').trigger('click')
    await flushPromises()
    expect(topics).toHaveBeenCalledWith('c9')
    const table = wrapper.get('[data-test=topics]')
    expect(table.text()).toContain('bygglov')
    expect(table.text()).toContain('412')
    expect(table.text()).not.toContain('Rule') // the rules are the owner's settings
  })

  it('shows the owner the topics with the rule that sets each one', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    vi.spyOn(api.collections, 'topics').mockResolvedValue({ collectionId: 'c1', topics: [
      { tag: 'bygglov', documents: 412, rules: [{ sourceId: 's1', sourceName: 'umea.se/bygga', on: 'path', match: 'www.umea.se/bygga/**' }] },
      { tag: 'altan', documents: 3, rules: [] },
    ] })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    await button(wrapper, 'Topics').trigger('click')
    await flushPromises()

    const table = wrapper.get('[data-test=topics]')
    expect(table.text()).toContain('Rule')
    expect(table.text()).toContain('www.umea.se/bygga/** · umea.se/bygga')
    expect(table.text()).toContain('No rule any more')
  })

  it('saves the sharing with selected departments, lists who uses it, and names the assistants when a narrowing is refused (V35)', async () => {
    signIn(['rag-department-admin'])
    const shared = collection({
      sharedWith: 'municipality', version: '7',
      usedBy: [
        collectionUser({ id: 'a1', name: 'Permits assistant', departmentId: 'd2', departmentName: 'Bygg och miljö', topics: ['bygglov'] }),
        collectionUser({ id: 'a2', name: 'Kundtjänst', departmentId: 'd3', departmentName: 'Kontaktcenter', published: false, draft: true }),
      ],
    })
    vi.spyOn(api.collections, 'get').mockResolvedValue(shared)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    vi.spyOn(api.organisation, 'departmentNames').mockResolvedValue([
      { id: 'd1', name: 'Kommunikation' }, { id: 'd2', name: 'Bygg och miljö' }, { id: 'd3', name: 'Kontaktcenter' },
    ])
    const save = vi.spyOn(api.collections, 'sharing')
      .mockRejectedValueOnce(new ApiError(409, 'PUT', {
        status: 409, title: 'Conflict', code: 'collection_shared_in_use', affected: ['Permits assistant (Bygg och miljö)'],
      } as never))
      .mockResolvedValueOnce({ ...shared, sharedWith: 'selected', sharedWithDepartmentIds: ['d2'], version: '8' })

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    await button(wrapper, 'Sharing').trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Only Kommunikation')
    expect((wrapper.get('#sharing-municipality').element as HTMLInputElement).checked).toBe(true)
    expect(wrapper.text()).toContain('Used by 2 assistants in 2 departments')
    const users = wrapper.get('[data-test=sharing-used-by]').text()
    expect(users).toContain('Permits assistant')
    expect(users).toContain('Bygg och miljö')
    expect(users).toContain('bygglov')
    expect(users).toContain('all') // a link without topics uses the whole knowledge base

    // Selected without a department says so and sends nothing; with one, the narrowing is refused and names the assistant.
    await wrapper.get('#sharing-selected').setValue(true)
    expect(wrapper.get('[data-test=sharing-departments]').text()).not.toContain('Kommunikation') // never the owner
    await wrapper.get('[data-test=save-sharing]').trigger('click')
    expect(wrapper.text()).toContain('Choose at least one department.')
    expect(save).not.toHaveBeenCalled()
    await wrapper.get('[data-test=sharing-departments] input[type=checkbox]').setValue(true)
    await wrapper.get('[data-test=save-sharing]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledWith('c1', { sharedWith: 'selected', departmentIds: ['d2'], version: '7' })
    expect(wrapper.get('[data-test=sharing-error]').text()).toContain('Assistants of other departments use this knowledge base')
    expect(wrapper.get('[data-test=sharing-error]').text()).toContain('Permits assistant (Bygg och miljö)')

    await wrapper.get('[data-test=save-sharing]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledTimes(2)
    expect(wrapper.find('[data-test=sharing-error]').exists()).toBe(false)
  })

  it('shows the sharing to an editor without letting them change it', async () => {
    signIn(['rag-editor'])
    vi.spyOn(api.collections, 'get').mockResolvedValue(base)
    vi.spyOn(api.collections, 'sources').mockResolvedValue([])
    vi.spyOn(api.organisation, 'departmentNames').mockResolvedValue([{ id: 'd1', name: 'Kommunikation' }])

    const wrapper = mount(CollectionView, { props: { id: 'c1' } })
    await flushPromises()
    await button(wrapper, 'Sharing').trigger('click')
    await flushPromises()

    expect((wrapper.get('#sharing-department').element as HTMLInputElement).disabled).toBe(true)
    expect(wrapper.find('[data-test=save-sharing]').exists()).toBe(false)
    expect(wrapper.text()).toContain('Only a department administrator can change the sharing.')
  })

  describe('Files tab (§5.5.7)', () => {
    const upload = (id: string, name: string): SourceListItem =>
      ({ id, collectionId: 'c1', type: 'upload', name, classification: 'internal', state: 'active', documents: 0, lastRun: null })
    const crawl: SourceListItem = { id: 's1', collectionId: 'c1', type: 'crawl', name: 'umea.se', classification: 'internal', state: 'active', documents: 0, lastRun: null }
    const full = (id: string, name: string) => ({ ...upload(id, name), settings: {}, version: '1' }) as unknown as Source

    function mockFiles() {
      vi.spyOn(api.sources, 'filesSummary').mockResolvedValue({ total: 0, processing: 0, indexed: 0, failed: 0, bytes: 0, lastUploadAt: null })
      return vi.spyOn(api.sources, 'files').mockResolvedValue({ items: [], next: null })
    }

    it('is shown for an upload source, opens from ?tab=files, and shows a reader the list without the drop zone', async () => {
      signIn(['rag-viewer'])
      route.query = { tab: 'files' }
      vi.spyOn(api.collections, 'get').mockResolvedValue(base)
      vi.spyOn(api.collections, 'sources').mockResolvedValue([crawl, upload('s2', 'Files')])
      const get = vi.spyOn(api.sources, 'get').mockResolvedValue(full('s2', 'Files'))
      const files = mockFiles()

      const wrapper = mount(CollectionView, { props: { id: 'c1' } })
      await flushPromises()

      expect(wrapper.findAll('[role=tab]').map((t) => t.text())).toEqual(expect.arrayContaining(['Files']))
      expect(wrapper.get('[role=tab][aria-selected=true]').text()).toBe('Files')
      expect(get).toHaveBeenCalledWith('s2')
      expect(files).toHaveBeenCalledWith('s2', expect.anything())
      expect(wrapper.find('[data-test=files]').exists()).toBe(true)
      expect(wrapper.find('[data-test=drop-zone]').exists()).toBe(false) // Read only
      expect(wrapper.find('#files-source').exists()).toBe(false) // one upload source: no picker
    })

    it('picks the upload source with a select when there are several, and links the folder hint to Topics', async () => {
      signIn(['rag-editor'])
      route.query = { tab: 'files' }
      vi.spyOn(api.collections, 'get').mockResolvedValue(base)
      vi.spyOn(api.collections, 'sources').mockResolvedValue([upload('s2', 'Files'), upload('s3', 'Blanketter')])
      const get = vi.spyOn(api.sources, 'get').mockImplementation(async (id) => full(id, id === 's2' ? 'Files' : 'Blanketter'))
      const files = mockFiles()
      vi.spyOn(api.collections, 'topics').mockResolvedValue({ collectionId: 'c1', topics: [] })

      const wrapper = mount(CollectionView, { props: { id: 'c1' } })
      await flushPromises()
      expect(wrapper.find('[data-test=drop-zone]').exists()).toBe(true)
      await wrapper.get('#files-source').setValue('s3')
      await flushPromises()
      expect(get).toHaveBeenLastCalledWith('s3')
      expect(files).toHaveBeenLastCalledWith('s3', expect.anything())

      await wrapper.get('[data-test=topics-link]').trigger('click')
      await flushPromises()
      expect(wrapper.get('[role=tab][aria-selected=true]').text()).toBe('Topics')
    })

    it('is not shown without an upload source; Add files on Sources creates one and opens the tab', async () => {
      signIn(['rag-editor'])
      route.query = { tab: 'files' } // e.g. after the source failed in the create drawer
      vi.spyOn(api.collections, 'get').mockResolvedValue({ ...base, classification: 'public' })
      const list = vi.spyOn(api.collections, 'sources').mockResolvedValueOnce([crawl]).mockResolvedValue([crawl, upload('s2', 'Files')])
      const addSource = vi.spyOn(api.collections, 'addSource').mockResolvedValue(full('s2', 'Files'))
      vi.spyOn(api.sources, 'get').mockResolvedValue(full('s2', 'Files'))
      mockFiles()

      const wrapper = mount(CollectionView, { props: { id: 'c1' } })
      await flushPromises()
      expect(wrapper.findAll('[role=tab]').map((t) => t.text())).not.toContain('Files')
      expect(wrapper.get('[role=tab][aria-selected=true]').text()).toContain('Sources')
      expect(wrapper.get('[data-test=no-files-source]').text()).toContain('Add files creates it.')

      await wrapper.get('[data-test=add-files]').trigger('click')
      await flushPromises()
      expect(addSource).toHaveBeenCalledWith('c1', { type: 'upload', name: 'Files', classification: 'public', settings: { piiMode: 'hold' } })
      expect(list).toHaveBeenCalledTimes(2)
      expect(wrapper.get('[role=tab][aria-selected=true]').text()).toBe('Files')
      expect(wrapper.find('[data-test=files]').exists()).toBe(true)
    })

    it('offers no Add files to a reader, nor once there is an upload source', async () => {
      signIn(['rag-viewer'])
      vi.spyOn(api.collections, 'get').mockResolvedValue(base)
      vi.spyOn(api.collections, 'sources').mockResolvedValue([crawl])
      const reader = mount(CollectionView, { props: { id: 'c1' } })
      await flushPromises()
      await button(reader, 'Sources (1)').trigger('click')
      expect(reader.find('[data-test=add-files]').exists()).toBe(false)

      signIn(['rag-editor'])
      vi.spyOn(api.collections, 'get').mockResolvedValue(base)
      vi.spyOn(api.collections, 'sources').mockResolvedValue([crawl, upload('s2', 'Files')])
      const editor = mount(CollectionView, { props: { id: 'c1' } })
      await flushPromises()
      await button(editor, 'Sources (2)').trigger('click')
      expect(editor.find('[data-test=add-files]').exists()).toBe(false)
      expect(editor.find('[data-test=add-source]').exists()).toBe(true)
    })
  })
})
