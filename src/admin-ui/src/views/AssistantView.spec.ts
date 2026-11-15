import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { Assistant, AssistantDraft, ClientSummary } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser, collectionSummary } from '@/test/fixtures'
import { button } from '@/test/helpers'
import { formatNumber } from '@/utils/format'
import { ASSISTANT, MODEL_AVAILABILITY } from '@/utils/labels'
import AssistantView from './AssistantView.vue'

// `?tab=` through a route the tests set; tab changes go to router.replace.
const route = vi.hoisted(() => ({ query: {} as Record<string, string> }))
const replace = vi.hoisted(() => vi.fn())
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRoute: () => route, useRouter: () => ({ push: vi.fn(), replace }) }))
beforeEach(() => {
  route.query = {}
  replace.mockReset()
})

const assistant: Assistant = {
  id: 'a1', slug: 'bygglov', name: 'Bygglovsassistenten', description: null, contactName: null, contactEmail: null, departmentId: 'd1', ownerTeamId: null,
  classification: 'internal', historyMode: 'none', retentionDays: 90, state: 'active', publishedVersion: null, generalKnowledge: null, rerankers: [],
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
  vi.spyOn(api.clients, 'list').mockResolvedValue([{ id: 'k1', name: 'Open WebUI (pilot)', kind: 'open-webui', classificationCeiling: 'internal', isEnabled: true, isManagedByDeployment: false, activeKeys: 1, lastUsedAt: null }])
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
    vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, rerankers: ['gateway'] })
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
    await wrapper.get('[data-test=use-model-output] input').setValue(false)
    await wrapper.get('#assistant-max-tokens').setValue('800')
    await wrapper.get('#assistant-temperature').setValue('0,2')
    await wrapper.get('[data-test=condense] input').setValue(false)
    // Rerank (B-12, B-16) is off by default and stored only when a reranker is chosen; here the platform has the gateway's.
    const rerank = wrapper.get('#assistant-rerank').element as HTMLSelectElement
    expect(rerank.value).toBe('')
    expect(rerank.disabled).toBe(false)
    expect([...rerank.options].filter((o) => o.disabled).map((o) => o.value)).toEqual(['chat'])
    await wrapper.get('#assistant-rerank').setValue('gateway')
    await wrapper.get('#assistant-language').setValue('')
    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()

    // Other keys of a step are kept (filters); an emptied field drops its key.
    expect(save).toHaveBeenCalledWith('a1', expect.objectContaining({
      gatewayModel: 'ume/chat-eu', systemPrompt: 'Du svarar invånare.',
      querySteps: {
        retrieval: { topK: 12, filters: { tags: ['x'] }, rerank: 'gateway' }, answer: { generalKnowledge: 'off', maxOutputTokens: 800, temperature: 0.2 }, condense: { enabled: false },
      },
    }))
  })

  it('disables the rerank choice with a hint while the platform has no rerank model, the usual setup (B-16)', async () => {
    signIn(['rag-editor'])
    mocks()
    const wrapper = mount(AssistantView, { props: { id: 'a1' }, attachTo: document.body })
    await flushPromises()

    const rerank = wrapper.get('#assistant-rerank').element as HTMLSelectElement
    expect(rerank.disabled).toBe(true)
    expect(rerank.value).toBe('')
    expect(wrapper.get('[data-test=advanced]').text()).toContain(ASSISTANT.rerankNone)
    expect(wrapper.get('[data-test=advanced]').text()).not.toContain(ASSISTANT.rerankHint)
  })

  it('keeps a stored reranker the platform has no model for, with a hint that the plain ranking is used (B-16)', async () => {
    signIn(['rag-editor'])
    mocks()
    vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, querySteps: { retrieval: { rerank: 'chat' } } })
    const wrapper = mount(AssistantView, { props: { id: 'a1' }, attachTo: document.body })
    await flushPromises()

    const rerank = wrapper.get('#assistant-rerank').element as HTMLSelectElement
    expect(rerank.disabled).toBe(false)
    expect(rerank.value).toBe('chat')
    expect([...rerank.options].filter((o) => o.disabled).map((o) => o.value)).toEqual(['gateway'])
    expect(wrapper.get('[data-test=advanced]').text()).toContain(ASSISTANT.rerankMissing)
  })

  describe('"Open in <client>" (§5.4 step 13, #363 point 2)', () => {
    const portalClients: ClientSummary[] = [
      { id: 'k1', name: 'Open WebUI (pilot)', kind: 'open-webui', classificationCeiling: 'internal', isEnabled: true, isManagedByDeployment: false, activeKeys: 1, lastUsedAt: null, portalUrl: 'https://chat.example.invalid' },
      { id: 'k2', name: 'Webb', kind: 'integration', classificationCeiling: 'public', isEnabled: true, isManagedByDeployment: false, activeKeys: 1, lastUsedAt: null, portalUrl: 'https://webb.example.invalid' },
      { id: 'k3', name: 'Integration utan UI', kind: 'integration', classificationCeiling: 'public', isEnabled: true, isManagedByDeployment: false, activeKeys: 1, lastUsedAt: null, portalUrl: null },
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

  it('offers every general knowledge level on a public assistant with a warning, and publishes after one extra confirmation (V36, #445)', async () => {
    signIn(['rag-department-admin'])
    mocks()
    vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, classification: 'public' })
    vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, querySteps: { answer: { generalKnowledge: 'supplement' } } })
    const unconfirmed = new ApiError(409, 'POST', { status: 409, code: 'general_knowledge_public_unconfirmed', params: { generalKnowledge: 'supplement' } })
    const publish = vi.spyOn(api.assistants, 'publish')
      .mockRejectedValueOnce(unconfirmed)
      .mockResolvedValueOnce({ ...assistant, classification: 'public', publishedVersion: 1, draft: null, version: 'a4' })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    const radios = wrapper.findAll<HTMLInputElement>('[data-test=general-knowledge] input[type=radio]')
    expect(radios.map((r) => r.element.disabled)).toEqual([false, false, false, false])
    expect(wrapper.get('[data-test=general-knowledge-public-warning]').text()).toBe(ASSISTANT.generalKnowledgePublicWarning)
    await wrapper.get('#assistant-general-knowledge-off').setValue(true)
    expect(wrapper.find('[data-test=general-knowledge-public-warning]').exists()).toBe(false)
    await wrapper.get('#assistant-general-knowledge-fallback').setValue(true)
    expect(wrapper.find('[data-test=general-knowledge-public-warning]').exists()).toBe(true)

    await wrapper.get('[data-test=publish]').trigger('click')
    await flushPromises()
    expect(confirm).toHaveBeenCalledWith(ASSISTANT.generalKnowledgeConfirm)
    expect(publish).toHaveBeenNthCalledWith(1, 'a1', { version: 'a3', draftVersion: 'd7', confirmBaseChanged: false, confirmGeneralKnowledge: false })
    expect(publish).toHaveBeenNthCalledWith(2, 'a1', { version: 'a3', draftVersion: 'd7', confirmBaseChanged: false, confirmGeneralKnowledge: true })
    expect(wrapper.get('[data-test=published]').text()).toBe('Published: version 1')
    confirm.mockRestore()
  })

  it('does not publish a public assistant with general knowledge when the extra confirmation is declined', async () => {
    signIn(['rag-department-admin'])
    mocks()
    vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, classification: 'public' })
    const publish = vi.spyOn(api.assistants, 'publish')
      .mockRejectedValueOnce(new ApiError(409, 'POST', { status: 409, code: 'general_knowledge_public_unconfirmed' }))
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()
    await wrapper.get('[data-test=publish]').trigger('click')
    await flushPromises()
    expect(publish).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-test=published]').exists()).toBe(false)
    confirm.mockRestore()
  })

  it('greys out a model the platform\'s key may not use at the assistant\'s ceiling, and asks at its level (#447)', async () => {
    signIn(['rag-department-admin'])
    mocks()
    const models = vi.spyOn(api.gateway, 'models').mockResolvedValue([
      { name: 'ume/chat-eu', kind: 'model', modelKind: 'chat', residencies: ['Eu'], available: true },
      { name: 'claude-opus-5-5', kind: 'model', modelKind: 'chat', residencies: ['Eu'], available: false },
      { name: 'bge-m3', kind: 'model', modelKind: 'embedding', residencies: ['OnPrem'], available: true },
    ])
    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    expect(models).toHaveBeenCalledWith('internal', undefined) // no gateway key: the platform key's availability (B-43)
    const options = [...wrapper.get<HTMLSelectElement>('#assistant-model').element.options].map((o) => ({ value: o.value, label: o.text, disabled: o.disabled }))
    expect(options).toContainEqual({ value: 'ume/chat-eu', label: 'ume/chat-eu', disabled: false })
    expect(options).toContainEqual({ value: 'claude-opus-5-5', label: `claude-opus-5-5 — ${MODEL_AVAILABILITY.notAvailable}`, disabled: true })
    expect(options.some((o) => o.value === 'bge-m3')).toBe(false)
    expect(wrapper.text()).not.toContain(MODEL_AVAILABILITY.unknown)
  })

  it('keeps every model choosable with a hint when the gateway could not be asked (#447)', async () => {
    signIn(['rag-department-admin'])
    mocks()
    vi.spyOn(api.gateway, 'models').mockResolvedValue([
      { name: 'ume/chat-eu', kind: 'model', modelKind: 'chat', residencies: ['Eu'], available: null },
      { name: 'claude-opus-5-5', kind: 'model', modelKind: 'chat', residencies: ['Eu'], available: null },
    ])
    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()
    const disabled = [...wrapper.get<HTMLSelectElement>('#assistant-model').element.options].filter((o) => o.value).map((o) => o.disabled)
    expect(disabled).toEqual([false, false])
    expect(wrapper.text()).toContain(MODEL_AVAILABILITY.unknown)
  })

  it('tests the draft, publishes it, and sets the audience', async () => {
    signIn(['rag-department-admin'])
    mocks()
    const test = vi.spyOn(api.assistants, 'test').mockResolvedValue({
      target: 'Draft', versionNumber: 1, durationMs: 812.4, condense: { used: false, outcome: null },
      retrieval: { collections: [], coverage: null, degraded: null, candidates: [{ rank: 1, documentId: 'x', title: 'Altan', urlOrFileName: null, headingPath: 'Bygglov › Altan', text: 'Du behöver bygglov…', cited: true }] },
      answer: { raw: 'Ja [1].', delivered: 'Ja [1].', outcome: 'answered', language: 'sv', citations: [] },
      gateway: { model: 'ume/chat-eu', residency: 'Eu', latencyMs: 700, inputTokens: 300, outputTokens: 5, costUsd: 0.0001, costEstimated: false },
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
    expect(publish).toHaveBeenCalledWith('a1', { version: 'a3', draftVersion: 'd7', confirmBaseChanged: false, confirmGeneralKnowledge: false })
    expect(wrapper.get('[data-test=published]').text()).toBe('Published: version 1')

    await button(wrapper, 'Audience').trigger('click')
    // "Who can use it" in plain words (admin design §5.5.4), from the saved audience.
    expect(wrapper.get('[data-test=who-can-use]').text()).toContain('Nobody yet')
    await wrapper.get('input[type=checkbox]').setValue(true)
    await wrapper.get('[data-test=save-audience]').trigger('click')
    await flushPromises()
    expect(audiences).toHaveBeenCalledWith('a1', { clientIds: ['k1'], version: 'a4' })
    expect(wrapper.get('[data-test=who-can-use]').text()).toContain('Open WebUI (pilot): everyone who can sign in to it.')
    // Adding an app is a platform admin's job: a department admin gets no link.
    expect(wrapper.find('[data-test=connect-app]').exists()).toBe(false)
  })

  it('links a platform admin from Who can use it to Add app, with this assistant ticked (admin design §5.9.1)', async () => {
    signIn(['rag-platform-admin'])
    mocks()
    const wrapper = mount(AssistantView, { props: { id: 'a1' }, attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
    await flushPromises()
    await button(wrapper, 'Audience').trigger('click')
    const link = wrapper.get('[data-test=connect-app]')
    expect(link.text()).toBe('Connect a new app')
    expect(wrapper.findAllComponents(RouterLinkStub).find((l) => l.find('[data-test=connect-app]').exists() || l.attributes('data-test') === 'connect-app')!.props('to'))
      .toEqual({ name: 'platform-clients', query: { connect: 'a1' } })
  })

  it('opens the tab named in ?tab= and keeps the tab in the address when it changes', async () => {
    signIn(['rag-editor'])
    mocks()
    route.query = { tab: 'versions' }
    const versions = vi.spyOn(api.assistants, 'versions').mockResolvedValue([])
    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    expect(wrapper.find('#assistant-panel-versions').exists()).toBe(true)
    expect(versions).toHaveBeenCalledTimes(1)

    await button(wrapper, 'Test').trigger('click')
    expect(replace).toHaveBeenLastCalledWith({ query: { tab: 'test' } })
    await button(wrapper, 'Draft').trigger('click')
    expect(replace).toHaveBeenLastCalledWith({ query: {} }) // the first tab leaves no ?tab=
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
    expect(restore).toHaveBeenCalledWith('a1', 2, 'a3', false)
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

  it('shows a general-knowledge answer of the test panel with its outcome', async () => {
    signIn(['rag-editor'])
    mocks()
    const caveat = '*Jag hittar inget om detta i kommunens källor. Svaret bygger på allmän kunskap och kan vara fel för Umeå kommun.*'
    vi.spyOn(api.assistants, 'test').mockResolvedValue({
      target: 'Draft', versionNumber: 1, durationMs: 500, condense: { used: false, outcome: null },
      retrieval: { collections: [], coverage: null, degraded: null, candidates: [] },
      answer: { raw: 'Ungefär 384 400 km.', delivered: `${caveat}\n\nUngefär 384 400 km.`, outcome: 'answered-general', language: 'sv', citations: [] },
      gateway: { model: 'ume/chat-eu', residency: 'Eu', latencyMs: 400, inputTokens: 100, outputTokens: 8, costUsd: 0.0001, costEstimated: false },
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

  describe('answer length (Decisions "Answer length from the model", admin design §5.5.4)', () => {
    function withLimits() {
      vi.spyOn(api.gateway, 'models').mockResolvedValue([
        { name: 'ume/chat-eu', kind: 'model', modelKind: 'chat', residencies: ['Eu'], contextWindow: 128_000, maxOutputTokens: 16_384 },
        { name: 'ume/chat', kind: 'route', modelKind: 'chat', residencies: ['OnPrem', 'Eu'] },
      ])
    }

    it('uses the model\'s limits by default, shows them, and stores no value while checked', async () => {
      signIn(['rag-editor'])
      mocks()
      withLimits()
      vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, gatewayModel: 'ume/chat-eu' })
      const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, warnings: [], version: 'd8' })
      const wrapper = mount(AssistantView, { props: { id: 'a1' } })
      await flushPromises()

      expect((wrapper.get('[data-test=use-model-output] input').element as HTMLInputElement).checked).toBe(true)
      expect((wrapper.get('[data-test=use-model-context] input').element as HTMLInputElement).checked).toBe(true)
      expect(wrapper.find('#assistant-max-tokens').exists()).toBe(false)
      expect(wrapper.get('[data-test=model-output-limit]').text()).toBe(ASSISTANT.modelLimit(formatNumber(16_384)))
      expect(wrapper.get('[data-test=model-context-limit]').text()).toBe(ASSISTANT.modelLimit(formatNumber(128_000)))

      await wrapper.get('[data-test=save-draft]').trigger('click')
      await flushPromises()
      const steps = save.mock.calls[0]![1].querySteps as { answer: Record<string, unknown> }
      expect(steps.answer).not.toHaveProperty('maxOutputTokens')
      expect(steps.answer).not.toHaveProperty('contextTokens')
    })

    it('unchecked, takes a number up to the model\'s limit', async () => {
      signIn(['rag-editor'])
      mocks()
      withLimits()
      vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, gatewayModel: 'ume/chat-eu' })
      const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, warnings: [], version: 'd8' })
      const wrapper = mount(AssistantView, { props: { id: 'a1' } })
      await flushPromises()

      await wrapper.get('[data-test=use-model-output] input').setValue(false)
      await wrapper.get('#assistant-max-tokens').setValue('20000')
      await wrapper.get('[data-test=save-draft]').trigger('click')
      await flushPromises()
      expect(save).not.toHaveBeenCalled()
      expect(wrapper.text()).toContain(ASSISTANT.rangeInvalid(64, 16_384))

      await wrapper.get('#assistant-max-tokens').setValue('8000')
      await wrapper.get('[data-test=use-model-context] input').setValue(false)
      await wrapper.get('#assistant-context-tokens').setValue('32768')
      await wrapper.get('[data-test=save-draft]').trigger('click')
      await flushPromises()
      expect((save.mock.calls[0]![1].querySteps as { answer: Record<string, unknown> }).answer).toMatchObject({ maxOutputTokens: 8000, contextTokens: 32768 })
    })

    it('shows the platform ceiling when it is lower than the model\'s limit, and bounds the override by it', async () => {
      signIn(['rag-editor'])
      mocks()
      withLimits()
      vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, maxAnswerTokens: 4000 })
      vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, gatewayModel: 'ume/chat-eu' })
      const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, warnings: [], version: 'd8' })
      const wrapper = mount(AssistantView, { props: { id: 'a1' } })
      await flushPromises()

      expect(wrapper.get('[data-test=answer-ceiling]').text()).toBe(ASSISTANT.answerCeiling(formatNumber(4000)))
      await wrapper.get('[data-test=use-model-output] input').setValue(false)
      await wrapper.get('#assistant-max-tokens').setValue('5000')
      await wrapper.get('[data-test=save-draft]').trigger('click')
      await flushPromises()
      expect(save).not.toHaveBeenCalled()
      expect(wrapper.text()).toContain(ASSISTANT.rangeInvalid(64, 4000))
    })

    it('does not show a platform ceiling above the model\'s limit', async () => {
      signIn(['rag-editor'])
      mocks()
      withLimits()
      vi.spyOn(api.assistants, 'get').mockResolvedValue({ ...assistant, maxAnswerTokens: 100_000 })
      vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, gatewayModel: 'ume/chat-eu' })
      const wrapper = mount(AssistantView, { props: { id: 'a1' } })
      await flushPromises()
      expect(wrapper.find('[data-test=answer-ceiling]').exists()).toBe(false)
    })

    it('opens a stored override unchecked, drops it when checked again, and says when the model\'s limit is unknown', async () => {
      signIn(['rag-editor'])
      mocks()
      vi.spyOn(api.assistants, 'draft').mockResolvedValue({ ...draft, gatewayModel: 'ume/chat-eu', querySteps: { answer: { maxOutputTokens: 800 } } })
      const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, warnings: [], version: 'd8' })
      const wrapper = mount(AssistantView, { props: { id: 'a1' } })
      await flushPromises()

      expect((wrapper.get('[data-test=use-model-output] input').element as HTMLInputElement).checked).toBe(false)
      expect((wrapper.get('#assistant-max-tokens').element as HTMLInputElement).value).toBe('800')
      expect(wrapper.get('[data-test=model-output-limit]').text()).toBe(ASSISTANT.modelLimitUnknown)

      await wrapper.get('[data-test=use-model-output] input').setValue(true)
      await wrapper.get('[data-test=save-draft]').trigger('click')
      await flushPromises()
      expect((save.mock.calls[0]![1].querySteps as { answer: Record<string, unknown> }).answer).not.toHaveProperty('maxOutputTokens')
    })

    it('the test panel says when the answer stopped at the length limit, and what limit was sent', async () => {
      signIn(['rag-editor'])
      mocks()
      vi.spyOn(api.assistants, 'test').mockResolvedValue({
        target: 'Draft', versionNumber: 1, durationMs: 500, condense: { used: false, outcome: null },
        retrieval: { collections: [], coverage: null, degraded: null, candidates: [] },
        answer: { raw: 'Ett långt svar', delivered: 'Ett långt svar', outcome: 'answered', language: 'sv', citations: [] },
        gateway: { model: 'ume/chat-eu', residency: 'Eu', latencyMs: 400, inputTokens: 100, outputTokens: 2000, maxTokens: 2000, maxTokensSource: 'model', finishReason: 'length', costUsd: 0.0012, costEstimated: true },
      })
      const wrapper = mount(AssistantView, { props: { id: 'a1' } })
      await flushPromises()
      await button(wrapper, 'Test').trigger('click')
      await wrapper.get('#assistant-question').setValue('Berätta allt om bygglov.')
      await wrapper.get('form').trigger('submit')
      await flushPromises()

      expect(wrapper.get('[data-test=length-stopped]').text()).toBe(ASSISTANT.lengthStopped)
      expect(wrapper.get('[data-test=max-tokens-sent]').text()).toBe(ASSISTANT.maxTokensSent(formatNumber(2000), ASSISTANT.maxTokensSources.model!))
      // B-39 (admin §4.9): the turn's cost over all its calls, "estimated" when any part is.
      expect(wrapper.get('[data-test=test-cost]').text()).toBe('Cost: 0,0012 USD (estimated)')
    })

    it('the test panel shows chat\'s reason when the assistant\'s key is unusable or its budget spent (B-44)', async () => {
      signIn(['rag-editor'])
      mocks()
      for (const [code, reason] of [
        ['test_assistant_unavailable', 'The assistant is unavailable: its gateway key is disabled.'],
        ['test_budget_exceeded', 'The department\'s budget (USD 1000 per month) is used up. Ask the department\'s administrator to raise it.'],
      ] as const) {
        vi.spyOn(api.assistants, 'test').mockRejectedValueOnce(new ApiError(503, 'POST', { status: 503, code, params: { reason } }))
        const wrapper = mount(AssistantView, { props: { id: 'a1' } })
        await flushPromises()
        await button(wrapper, 'Test').trigger('click')
        await wrapper.get('#assistant-question').setValue('Hej?')
        await wrapper.get('form').trigger('submit')
        await flushPromises()
        expect(wrapper.text()).toContain(reason)
        wrapper.unmount()
      }
    })

    it('the test panel shows no length notice for a complete answer, and the provider\'s default when no limit was sent', async () => {
      signIn(['rag-editor'])
      mocks()
      vi.spyOn(api.assistants, 'test').mockResolvedValue({
        target: 'Draft', versionNumber: 1, durationMs: 500, condense: { used: false, outcome: null },
        retrieval: { collections: [], coverage: null, degraded: null, candidates: [] },
        answer: { raw: 'Kort.', delivered: 'Kort.', outcome: 'answered', language: 'sv', citations: [] },
        gateway: { model: 'ume/chat-eu', residency: 'Eu', latencyMs: 400, inputTokens: 100, outputTokens: 2, maxTokens: null, maxTokensSource: 'provider', finishReason: 'stop', costUsd: 0.00002, costEstimated: false },
      })
      const wrapper = mount(AssistantView, { props: { id: 'a1' } })
      await flushPromises()
      await button(wrapper, 'Test').trigger('click')
      await wrapper.get('#assistant-question').setValue('Hej?')
      await wrapper.get('form').trigger('submit')
      await flushPromises()

      expect(wrapper.find('[data-test=length-stopped]').exists()).toBe(false)
      expect(wrapper.get('[data-test=max-tokens-sent]').text()).toBe(ASSISTANT.maxTokensSent(null, ASSISTANT.maxTokensSources.provider!))
      expect(wrapper.get('[data-test=test-cost]').text()).toBe('Cost: < 0,0001 USD') // #625 review: a cheap turn must not look free
    })
  })
})
