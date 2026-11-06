import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Assistant, AssistantDraft, ClientSummary } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser, collectionSummary } from '@/test/fixtures'
import { button } from '@/test/helpers'
import AssistantView from './AssistantView.vue'

const assistant: Assistant = {
  id: 'a1', slug: 'bygglov', name: 'Bygglovsassistenten', description: null, contactName: null, contactEmail: null, departmentId: 'd1', ownerTeamId: null,
  classification: 'internal', historyMode: 'none', retentionDays: 90, state: 'active', publishedVersion: null, generalKnowledge: null,
  draft: { number: 1, updatedAt: '2026-11-01T10:00:00Z', baseChanged: false, version: 'd7' }, audienceClientIds: [], updatedAt: '2026-11-01T10:00:00Z',
  updatedBy: null, version: 'a3',
}

const draft: AssistantDraft = {
  number: 1, gatewayModel: null, systemPrompt: '', querySteps: {}, notFound: {}, collectionLinks: [], updatedAt: '', version: 'd7',
  warnings: [{ code: 'assistant_contact_missing', params: null, affected: [] }],
}

function signIn(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
}

function mocks() {
  vi.spyOn(api.assistants, 'get').mockResolvedValue(assistant)
  vi.spyOn(api.assistants, 'draft').mockResolvedValue(draft)
  vi.spyOn(api.gateway, 'models').mockResolvedValue([
    { name: 'bge-m3', kind: 'model', modelKind: 'embedding', residencies: ['OnPrem'] },
    { name: 'ume/chat-eu', kind: 'model', modelKind: 'chat', residencies: ['Eu'] },
    { name: 'ume/chat', kind: 'route', modelKind: 'chat', residencies: ['OnPrem', 'Eu'] },
  ])
  vi.spyOn(api.collections, 'list').mockResolvedValue({ total: 2, items: [
    collectionSummary(),
    collectionSummary({ id: 'c2', slug: 'annan', name: 'Annan förvaltning', departmentId: 'd2', departmentName: 'Annan', shared: false }),
  ] })
  vi.spyOn(api.clients, 'list').mockResolvedValue([{ id: 'k1', name: 'Open WebUI (pilot)', kind: 'open-webui', classificationCeiling: 'internal', isEnabled: true }])
}

describe('Assistant (admin design §5.5.4; #278 slice 3)', () => {
  it('saves the draft with model, instructions and a knowledge base picked with a topic, and shows the V-rule warnings in plain words', async () => {
    signIn(['rag-editor'])
    mocks()
    const list = vi.spyOn(api.collections, 'list')
    const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, gatewayModel: 'ume/chat-eu', warnings: [], version: 'd8' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' }, attachTo: document.body })
    await flushPromises()

    expect(wrapper.get('[data-test=warnings]').text()).toContain('Add a description and a contact before you publish.')
    expect(wrapper.text()).toContain('Model name in Open WebUI: bygglov')
    const options = wrapper.findAll('#assistant-model option').map((o) => o.text())
    expect(options).toContain('ume/chat-eu')
    expect(options).toContain('ume/chat') // a route alias is a chat model
    expect(options).not.toContain('bge-m3') // embedding models are not answer models (#363 point 1): the mirror's modelKind, not kind
    // The picker reads every knowledge base the user may see (view=all): Ours is the assistant's department, Shared with us
    // the others, each with level, document count and topics.
    expect(list).toHaveBeenCalledWith({ pageSize: 200, view: 'all' })
    expect(wrapper.text()).toContain('No knowledge base is linked yet.')
    await wrapper.get('[data-test=add-knowledge]').trigger('click')
    await flushPromises()
    const picker = () => document.body.querySelector('[data-test=picker]')!.textContent!
    expect(picker()).toContain('Bygglov')
    expect(picker()).toContain('Stadsbyggnad · Internal · 0 documents · not published')
    expect(picker()).not.toContain('Annan förvaltning')
    document.body.querySelector<HTMLButtonElement>('[role=tab][id$=-picker-tab-shared]')!.click()
    await flushPromises()
    expect(picker()).toContain('Annan förvaltning')
    expect(picker()).not.toContain('Bygglov')
    document.body.querySelector<HTMLButtonElement>('[role=tab][id$=-picker-tab-mine]')!.click()
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[aria-label="Add Bygglov"]')!.click()
    await flushPromises()

    const link = wrapper.get('[data-test=link-c1]')
    expect(link.text()).toContain('Bygglov (not published)')
    expect(link.text()).toContain('All topics')
    await link.get('input').setValue('bygglov')
    await link.get('form').trigger('submit')
    expect(link.text()).not.toContain('All topics')

    await wrapper.get('#assistant-model').setValue('ume/chat-eu')
    await wrapper.get('#assistant-prompt').setValue('Svara kort.')
    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()

    expect(save).toHaveBeenCalledWith('a1', {
      gatewayModel: 'ume/chat-eu', systemPrompt: 'Svara kort.', collectionLinks: [{ collectionId: 'c1', tags: ['bygglov'] }], querySteps: { answer: { generalKnowledge: 'off' } }, notFound: {}, version: 'd7',
    })
    expect(wrapper.find('[data-test=warnings]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows a shared knowledge base with its owner, and removes a topic or the link', async () => {
    signIn(['rag-editor'])
    mocks()
    vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, collectionLinks: [{ collectionId: 'c2', tags: ['avfall', 'kompost'] }, { collectionId: 'c1', tags: [] }] })
    const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, warnings: [], version: 'd8' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=link-c2]').text()).toContain('Annan förvaltning (Annan, shared)')
    await wrapper.get('[aria-label="Remove the topic avfall"]').trigger('click')
    await wrapper.get('[aria-label="Remove Bygglov"]').trigger('click')
    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()

    expect(save.mock.calls[0]![1].collectionLinks).toEqual([{ collectionId: 'c2', tags: ['kompost'] }])
  })

  it('sends each link back with the topics it was loaded with', async () => {
    signIn(['rag-editor'])
    mocks()
    vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, gatewayModel: 'ume/chat-eu', collectionLinks: [{ collectionId: 'c1', tags: ['bygglov', 'altan'] }] })
    const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, warnings: [], version: 'd8' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()
    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()

    expect(save.mock.calls[0]![1].collectionLinks).toEqual([{ collectionId: 'c1', tags: ['bygglov', 'altan'] }])
  })

  it('keeps the model and the query settings in a collapsed Advanced section, and saves from it', async () => {
    signIn(['rag-editor'])
    mocks()
    vi.spyOn(api.assistants, 'draft').mockResolvedValue({
      ...draft, systemPrompt: 'Du svarar invånare.', querySteps: { retrieval: { topK: 8, filters: { tags: ['x'] } }, answer: { generalKnowledge: 'off', language: 'sv' } },
    })
    const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, warnings: [], version: 'd8' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    // The first draft's instructions (the platform default) are on the Draft tab; the model is under Advanced.
    expect((wrapper.get('#assistant-prompt').element as HTMLTextAreaElement).value).toBe('Du svarar invånare.')
    const advanced = wrapper.get('[data-test=advanced]')
    expect((advanced.element as HTMLDetailsElement).open).toBe(false)
    expect(advanced.get('summary').text()).toBe('Advanced: model, follow-up questions, history, length')
    expect(advanced.find('#assistant-model').exists()).toBe(true)
    expect((wrapper.get('#assistant-top-k').element as HTMLInputElement).value).toBe('8')
    expect((wrapper.get('#assistant-language').element as HTMLSelectElement).value).toBe('sv')

    await advanced.get('summary').trigger('click')
    await wrapper.get('#assistant-model').setValue('ume/chat-eu')
    await wrapper.get('#assistant-top-k').setValue('99')
    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()
    expect(save).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('Enter a number from 1 to 50, or leave it empty.')

    await wrapper.get('#assistant-top-k').setValue('12')
    await wrapper.get('#assistant-max-tokens').setValue('800')
    await wrapper.get('#assistant-temperature').setValue('0,2')
    await wrapper.get('[data-test=condense] input').setValue(false)
    await wrapper.get('#assistant-language').setValue('')
    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()

    // Other keys of a step are kept (filters); an emptied field drops its key.
    expect(save).toHaveBeenCalledWith('a1', expect.objectContaining({
      gatewayModel: 'ume/chat-eu', systemPrompt: 'Du svarar invånare.',
      querySteps: {
        retrieval: { topK: 12, filters: { tags: ['x'] } }, answer: { generalKnowledge: 'off', maxOutputTokens: 800, temperature: 0.2 }, condense: { enabled: false },
      },
    }))
  })

  describe('"Open in <client>" (§5.4 step 13, #363 point 2)', () => {
    const portalClients: ClientSummary[] = [
      { id: 'k1', name: 'Open WebUI (pilot)', kind: 'open-webui', classificationCeiling: 'internal', isEnabled: true, portalUrl: 'https://chat.example.invalid' },
      { id: 'k2', name: 'Webb', kind: 'integration', classificationCeiling: 'public', isEnabled: true, portalUrl: 'https://webb.example.invalid' },
      { id: 'k3', name: 'Integration utan UI', kind: 'integration', classificationCeiling: 'public', isEnabled: true, portalUrl: null },
    ]

    async function mountWith(overrides: Partial<Assistant>) {
      signIn(['rag-viewer'])
      mocks()
      vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, ...overrides })
      vi.spyOn(api.clients, 'list').mockResolvedValue(portalClients)
      const wrapper = mount(AssistantView, { props: { id: 'a1' } })
      await flushPromises()
      return wrapper
    }

    it('links a published assistant to each audience client\'s own UI, in a new tab that the link names', async () => {
      const wrapper = await mountWith({ publishedVersion: 2, audienceClientIds: ['k1', 'k3'] })

      const links = wrapper.findAll('[data-test=portal]')
      expect(links).toHaveLength(1) // k2 is not in the audience, k3 has no PortalUrl
      const link = links[0]!
      expect(link.element.tagName).toBe('A')
      expect(link.attributes('href')).toBe('https://chat.example.invalid')
      expect(link.attributes('target')).toBe('_blank')
      expect(link.attributes('rel')).toContain('noopener')
      // The accessible name says where it goes and that it opens a new tab; the icon is decorative.
      expect(link.get('[aria-hidden=true]').text()).toBe('open_in_new')
      expect(link.text().replace('open_in_new', '').replace(/\s+/g, ' ').trim()).toBe('Open in Open WebUI (pilot) (opens in a new tab)')
      expect(link.get('.sr-only').text()).toBe('(opens in a new tab)')
    })

    it('shows no link while the assistant has only a draft', async () => {
      const wrapper = await mountWith({ publishedVersion: null, audienceClientIds: ['k1'] })
      expect(wrapper.find('[data-test=portal]').exists()).toBe(false)
    })

    it('shows no link when the audience client has no PortalUrl', async () => {
      const wrapper = await mountWith({ publishedVersion: 2, audienceClientIds: ['k3'] })
      expect(wrapper.find('[data-test=portal]').exists()).toBe(false)
    })

    it('shows no link once the assistant is archived', async () => {
      const wrapper = await mountWith({ publishedVersion: 2, state: 'archived', audienceClientIds: ['k1'] })
      expect(wrapper.find('[data-test=portal]').exists()).toBe(false)
    })
  })

  it('with no draft, shows the published version\'s settings and saves them as the new draft', async () => {
    signIn(['rag-editor'])
    mocks()
    vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, publishedVersion: 3, draft: null })
    const draftCall = vi.spyOn(api.assistants, 'draft')
    const version = vi.spyOn(api.assistants, 'version').mockResolvedValue({
      number: 3, state: 'published', gatewayModel: 'ume/chat-eu', systemPrompt: 'Svara kort.', querySteps: {}, notFound: {}, collectionLinks: [{ collectionId: 'c1', tags: [] }],
      restoredFrom: null, createdAt: '2026-11-01T10:00:00Z', createdBy: 'anna', publishedAt: '2026-11-01T10:00:00Z', publishedBy: 'anna',
    })
    const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, number: 4, gatewayModel: 'ume/chat-eu', systemPrompt: 'Svara kort.', collectionLinks: [{ collectionId: 'c1', tags: [] }], warnings: [], version: 'd1' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    expect(version).toHaveBeenCalledWith('a1', 3)
    expect(draftCall).not.toHaveBeenCalled()
    expect(wrapper.get('[data-test=draft-from]').text()).toBe('Editing starts a new draft from version 3.')
    expect((wrapper.get('#assistant-model').element as HTMLSelectElement).value).toBe('ume/chat-eu')
    expect((wrapper.get('#assistant-prompt').element as HTMLTextAreaElement).value).toBe('Svara kort.')
    expect(wrapper.find('[data-test=link-c1]').exists()).toBe(true)

    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledWith('a1', {
      gatewayModel: 'ume/chat-eu', systemPrompt: 'Svara kort.', collectionLinks: [{ collectionId: 'c1', tags: [] }], querySteps: { answer: { generalKnowledge: 'off' } }, notFound: {}, version: null,
    })
  })

  it('tests the draft, publishes it, and sets the audience', async () => {
    signIn(['rag-department-admin'])
    mocks()
    const test = vi.spyOn(api.assistants, 'test').mockResolvedValue({
      target: 'Draft', versionNumber: 1, durationMs: 812.4, condense: { used: false, outcome: null },
      retrieval: { collections: [], coverage: null, degraded: null, candidates: [{ rank: 1, documentId: 'x', title: 'Altan', urlOrFileName: null, headingPath: 'Bygglov › Altan', text: 'Du behöver bygglov…', cited: true }] },
      answer: { raw: 'Ja [1].', delivered: 'Ja [1].', outcome: 'answered', language: 'sv', citations: [] },
      gateway: { model: 'ume/chat-eu', residency: 'Eu', latencyMs: 700, inputTokens: 300, outputTokens: 5 },
    })
    const publish = vi.spyOn(api.assistants, 'publish').mockResolvedValue({ ...assistant, publishedVersion: 1, draft: null, version: 'a4' })
    const audiences = vi.spyOn(api.assistants, 'audiences').mockResolvedValue({ ...assistant, publishedVersion: 1, draft: null, audienceClientIds: ['k1'], version: 'a5' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    await button(wrapper, 'Test').trigger('click')
    await wrapper.get('#assistant-question').setValue('Behöver jag bygglov för en altan?')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(test).toHaveBeenCalledWith('a1', { question: 'Behöver jag bygglov för en altan?' })
    expect(wrapper.get('[data-test=answer]').text()).toContain('Ja [1].')
    expect(wrapper.text()).toContain('ume/chat-eu · EU · 812 ms')
    expect(wrapper.text()).toContain('Cited')

    await wrapper.get('[data-test=publish]').trigger('click')
    await flushPromises()
    expect(publish).toHaveBeenCalledWith('a1', { version: 'a3', draftVersion: 'd7', confirmBaseChanged: false })
    expect(wrapper.get('[data-test=published]').text()).toBe('Published: version 1')

    await button(wrapper, 'Audience').trigger('click')
    // "Who can use it" in plain words (admin design §5.5.4), from the saved audience.
    expect(wrapper.get('[data-test=who-can-use]').text()).toContain('Nobody yet')
    await wrapper.get('input[type=checkbox]').setValue(true)
    await wrapper.get('[data-test=save-audience]').trigger('click')
    await flushPromises()
    expect(audiences).toHaveBeenCalledWith('a1', { clientIds: ['k1'], version: 'a4' })
    expect(wrapper.get('[data-test=who-can-use]').text()).toContain('Open WebUI (pilot): everyone who can sign in to it.')
  })

  it('restores an earlier version after one confirmation, keeping the draft', async () => {
    signIn(['rag-editor'])
    mocks()
    vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, publishedVersion: 3 })
    const versions = vi.spyOn(api.assistants, 'versions').mockResolvedValue([
      { number: 4, state: 'draft', createdAt: '2026-11-01T12:00:00Z', createdBy: 'anna', publishedAt: null, publishedBy: null, restoredFrom: null },
      { number: 3, state: 'published', createdAt: '2026-11-01T11:00:00Z', createdBy: 'anna', publishedAt: '2026-11-01T11:00:00Z', publishedBy: 'anna', restoredFrom: 1 },
      { number: 2, state: 'superseded', createdAt: '2026-10-30T09:00:00Z', createdBy: 'bo', publishedAt: '2026-10-30T09:00:00Z', publishedBy: 'bo', restoredFrom: null },
    ])
    const restore = vi.spyOn(api.assistants, 'restore').mockResolvedValue({ ...assistant, publishedVersion: 5, version: 'a9' })
    const wrapper = mount(AssistantView, { props: { id: 'a1' }, attachTo: document.body })
    await flushPromises()

    await button(wrapper, 'Versions').trigger('click')
    await flushPromises()
    const table = wrapper.get('[data-test=versions]').text()
    expect(table).toContain('restored from version 1')
    expect(table).toContain('Earlier')
    expect(wrapper.find('[data-test=restore-3]').exists()).toBe(false) // the live version
    expect(wrapper.find('[data-test=restore-4]').exists()).toBe(false) // the draft is published, not restored

    await wrapper.get('[data-test=restore-2]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('Version 2 is published again as a new version. Your draft is kept.')
    const confirm = [...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === 'Restore').at(-1)!
    confirm.click()
    await flushPromises()
    expect(restore).toHaveBeenCalledWith('a1', 2, 'a3')
    expect(versions).toHaveBeenCalledTimes(2)
    expect(wrapper.get('[data-test=published]').text()).toBe('Published: version 5')
    wrapper.unmount()
  })

  it('archives and brings back the assistant, and sets retention within the level\'s maximum', async () => {
    signIn(['rag-department-admin'])
    mocks()
    const archive = vi.spyOn(api.assistants, 'archive').mockResolvedValue({ ...assistant, state: 'archived', version: 'a4' })
    const unarchive = vi.spyOn(api.assistants, 'unarchive').mockResolvedValue({ ...assistant, state: 'active', version: 'a5' })
    vi.spyOn(api.dataProtection, 'get').mockResolvedValue({
      classification: 'internal', allowedHistoryModes: ['none'], defaultHistoryMode: 'none', retentionDefaultDays: 90, retentionMaxDays: 180, version: '1',
    })
    const retention = vi.spyOn(api.assistants, 'retention').mockResolvedValue({ ...assistant, retentionDays: 30, version: 'a6' })
    const wrapper = mount(AssistantView, { props: { id: 'a1' }, attachTo: document.body })
    await flushPromises()

    await wrapper.get('[data-test=archive]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('The assistant disappears from all clients.')
    ;[...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === 'Archive').at(-1)!.click()
    await flushPromises()
    expect(archive).toHaveBeenCalledWith('a1', 'a3')
    expect(wrapper.get('[data-test=archived]').text()).toBe('Archived')
    await wrapper.get('[data-test=unarchive]').trigger('click')
    await flushPromises()
    expect(unarchive).toHaveBeenCalledWith('a1', 'a4')
    expect(wrapper.find('[data-test=archived]').exists()).toBe(false)

    await button(wrapper, 'Storage').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('At most 180 days for the information class.')
    expect(wrapper.text()).toContain('Questions and answers are not saved')
    await wrapper.get('#assistant-retention').setValue('365')
    await wrapper.get('[data-test=save-storage]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Enter a whole number from 1 to 180.')
    expect(retention).not.toHaveBeenCalled()
    await wrapper.get('#assistant-retention').setValue('30')
    await wrapper.get('[data-test=save-storage]').trigger('click')
    await flushPromises()
    expect(retention).toHaveBeenCalledWith('a1', { historyMode: 'none', retentionDays: 30, version: 'a5' })
    wrapper.unmount()
  })

  it('sets general knowledge on the draft, keeps the other query steps, and shows the badge (P1b item 40)', async () => {
    signIn(['rag-editor'])
    mocks()
    vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, publishedVersion: 2, generalKnowledge: 'fallback' })
    vi.spyOn(api.assistants, 'draft').mockResolvedValue({
      ...draft, querySteps: { retrieval: { topK: 8 }, answer: { language: 'sv' } }, notFound: { sv: 'Inget svar.', links: [] },
    })
    const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, querySteps: { answer: { generalKnowledge: 'fallback' } }, warnings: [], version: 'd8' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=general-knowledge-badge]').text()).toBe('General knowledge')
    const group = wrapper.get('[data-test=general-knowledge]')
    expect(group.get('legend').text()).toBe('General knowledge')
    expect(group.findAll('input[type=radio]').map((r) => (r.element as HTMLInputElement).value)).toEqual(['off', 'supplement', 'fallback', 'open'])
    expect((wrapper.get('#assistant-general-knowledge-off').element as HTMLInputElement).checked).toBe(true) // unset reads as off
    expect(group.text()).toContain('Fallback: as Supplement, and answer from general knowledge when nothing is found')

    await wrapper.get('#assistant-general-knowledge-fallback').setValue(true)
    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()
    expect(save).toHaveBeenCalledWith('a1', expect.objectContaining({
      querySteps: { retrieval: { topK: 8 }, answer: { language: 'sv', generalKnowledge: 'fallback' } }, notFound: { sv: 'Inget svar.', links: [] },
    }))
    expect((wrapper.get('#assistant-general-knowledge-fallback').element as HTMLInputElement).checked).toBe(true)
  })

  it('offers only Off for a public assistant, with the reason (V36)', async () => {
    signIn(['rag-editor'])
    mocks()
    vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, classification: 'public' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    const disabled = ['off', 'supplement', 'fallback', 'open'].map((g) => (wrapper.get(`#assistant-general-knowledge-${g}`).element as HTMLInputElement).disabled)
    expect(disabled).toEqual([false, true, true, true])
    expect(wrapper.get('[data-test=general-knowledge]').text()).toContain('General knowledge needs the class Internal or higher.')
    expect(wrapper.find('[data-test=general-knowledge-badge]').exists()).toBe(false)
  })

  it('shows a general-knowledge answer of the test panel with its outcome', async () => {
    signIn(['rag-editor'])
    mocks()
    const caveat = '*Jag hittar inget om detta i kommunens källor. Svaret bygger på allmän kunskap och kan vara fel för Umeå kommun.*'
    vi.spyOn(api.assistants, 'test').mockResolvedValue({
      target: 'Draft', versionNumber: 1, durationMs: 500, condense: { used: false, outcome: null },
      retrieval: { collections: [], coverage: null, degraded: null, candidates: [] },
      answer: { raw: 'Ungefär 384 400 km.', delivered: `${caveat}\n\nUngefär 384 400 km.`, outcome: 'answered-general', language: 'sv', citations: [] },
      gateway: { model: 'ume/chat-eu', residency: 'Eu', latencyMs: 400, inputTokens: 100, outputTokens: 8 },
    })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()
    await button(wrapper, 'Test').trigger('click')
    await wrapper.get('#assistant-question').setValue('Hur långt är det till månen?')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    const answer = wrapper.get('[data-test=answer]').text()
    expect(answer).toContain('Answered from general knowledge')
    expect(answer).toContain('Jag hittar inget om detta i kommunens källor.')
  })
})
