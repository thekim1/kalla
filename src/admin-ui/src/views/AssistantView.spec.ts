import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Assistant, AssistantDraft } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import { button } from '@/test/helpers'
import AssistantView from './AssistantView.vue'

const assistant: Assistant = {
  id: 'a1', slug: 'bygglov', name: 'Bygglovsassistenten', description: null, contactName: null, contactEmail: null, departmentId: 'd1', ownerTeamId: null,
  classification: 'internal', historyMode: 'none', retentionDays: 90, state: 'active', publishedVersion: null,
  draft: { number: 1, updatedAt: '2026-11-01T10:00:00Z', baseChanged: false, version: 'd7' }, audienceClientIds: [], updatedAt: '2026-11-01T10:00:00Z',
  updatedBy: null, version: 'a3',
}

const draft: AssistantDraft = {
  number: 1, gatewayModel: null, systemPrompt: '', querySteps: {}, notFound: {}, collectionIds: [], updatedAt: '', version: 'd7',
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
  vi.spyOn(api.gateway, 'models').mockResolvedValue([{ name: 'ume/chat-eu', kind: 'chat', residencies: ['Eu'] }, { name: 'bge-m3', kind: 'embedding', residencies: ['OnPrem'] }])
  vi.spyOn(api.collections, 'list').mockResolvedValue({ total: 2, items: [
    { id: 'c1', slug: 'bygglov', name: 'Bygglov', departmentId: 'd1', classification: 'internal', publishedAt: null },
    { id: 'c2', slug: 'annan', name: 'Annan förvaltning', departmentId: 'd2', classification: 'internal', publishedAt: null },
  ] })
  vi.spyOn(api.clients, 'list').mockResolvedValue([{ id: 'k1', name: 'Open WebUI (pilot)', kind: 'open-webui', classificationCeiling: 'internal', isEnabled: true }])
}

describe('Assistent (admin design §5.5.4; #278 slice 3)', () => {
  it('saves the draft with model, instructions and collections and shows the V-rule warnings in Swedish', async () => {
    signIn(['rag-editor'])
    mocks()
    const save = vi.spyOn(api.assistants, 'saveDraft').mockResolvedValue({ ...draft, gatewayModel: 'ume/chat-eu', warnings: [], version: 'd8' })

    const wrapper = mount(AssistantView, { props: { id: 'a1' } })
    await flushPromises()

    expect(wrapper.get('[data-test=warnings]').text()).toContain('Lägg till en beskrivning och en kontakt innan du publicerar.')
    expect(wrapper.text()).toContain('Modellnamn i Open WebUI: bygglov')
    const options = wrapper.findAll('#assistant-model option').map((o) => o.text())
    expect(options).toContain('ume/chat-eu')
    expect(options).not.toContain('bge-m3') // embedding models are not answer models
    expect(wrapper.text()).toContain('Bygglov (ej publicerad)')
    expect(wrapper.text()).not.toContain('Annan förvaltning') // only the assistant's department's collections

    await wrapper.get('#assistant-model').setValue('ume/chat-eu')
    await wrapper.get('#assistant-prompt').setValue('Svara kort.')
    await wrapper.findAll('input[type=checkbox]')[0]!.setValue(true)
    await wrapper.get('[data-test=save-draft]').trigger('click')
    await flushPromises()

    expect(save).toHaveBeenCalledWith('a1', { gatewayModel: 'ume/chat-eu', systemPrompt: 'Svara kort.', collectionIds: ['c1'], version: 'd7' })
    expect(wrapper.find('[data-test=warnings]').exists()).toBe(false)
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

    await button(wrapper, 'Testa').trigger('click')
    await wrapper.get('#assistant-question').setValue('Behöver jag bygglov för en altan?')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(test).toHaveBeenCalledWith('a1', { question: 'Behöver jag bygglov för en altan?' })
    expect(wrapper.get('[data-test=answer]').text()).toContain('Ja [1].')
    expect(wrapper.text()).toContain('ume/chat-eu · Eu · 812 ms')
    expect(wrapper.text()).toContain('Citerad')

    await wrapper.get('[data-test=publish]').trigger('click')
    await flushPromises()
    expect(publish).toHaveBeenCalledWith('a1', { version: 'a3', draftVersion: 'd7', confirmBaseChanged: false })
    expect(wrapper.get('[data-test=published]').text()).toBe('Publicerad: version 1')

    await button(wrapper, 'Målgrupp').trigger('click')
    await wrapper.get('input[type=checkbox]').setValue(true)
    await wrapper.get('[data-test=save-audience]').trigger('click')
    await flushPromises()
    expect(audiences).toHaveBeenCalledWith('a1', { clientIds: ['k1'], version: 'a4' })
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

    await button(wrapper, 'Versioner').trigger('click')
    await flushPromises()
    const table = wrapper.get('[data-test=versions]').text()
    expect(table).toContain('återställd från version 1')
    expect(table).toContain('Tidigare')
    expect(wrapper.find('[data-test=restore-3]').exists()).toBe(false) // the live version
    expect(wrapper.find('[data-test=restore-4]').exists()).toBe(false) // the draft is published, not restored

    await wrapper.get('[data-test=restore-2]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('Version 2 publiceras igen som en ny version. Ditt utkast finns kvar.')
    const confirm = [...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === 'Återställ').at(-1)!
    confirm.click()
    await flushPromises()
    expect(restore).toHaveBeenCalledWith('a1', 2, 'a3')
    expect(versions).toHaveBeenCalledTimes(2)
    expect(wrapper.get('[data-test=published]').text()).toBe('Publicerad: version 5')
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
    expect(document.body.textContent).toContain('Assistenten försvinner från alla klienter.')
    ;[...document.body.querySelectorAll('button')].filter((b) => b.textContent?.trim() === 'Arkivera').at(-1)!.click()
    await flushPromises()
    expect(archive).toHaveBeenCalledWith('a1', 'a3')
    expect(wrapper.get('[data-test=archived]').text()).toBe('Arkiverad')
    await wrapper.get('[data-test=unarchive]').trigger('click')
    await flushPromises()
    expect(unarchive).toHaveBeenCalledWith('a1', 'a4')
    expect(wrapper.find('[data-test=archived]').exists()).toBe(false)

    await button(wrapper, 'Lagring').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Högst 180 dagar för informationsklassen.')
    expect(wrapper.text()).toContain('Frågor och svar sparas inte')
    await wrapper.get('#assistant-retention').setValue('365')
    await wrapper.get('[data-test=save-storage]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('Skriv ett heltal från 1 till 180.')
    expect(retention).not.toHaveBeenCalled()
    await wrapper.get('#assistant-retention').setValue('30')
    await wrapper.get('[data-test=save-storage]').trigger('click')
    await flushPromises()
    expect(retention).toHaveBeenCalledWith('a1', { historyMode: 'none', retentionDays: 30, version: 'a5' })
    wrapper.unmount()
  })
})
