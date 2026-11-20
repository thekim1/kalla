import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { PLATFORM } from '@/utils/labels'
import SettingsSectionForm from './SettingsSectionForm.vue'
import { resetOpenApiCache } from './schema'

const schema = { properties: { value: { type: 'object' } } }
const doc = { paths: { '/api/settings/chat': { put: { requestBody: { content: { 'application/json': { schema } } } } } } }

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
  document.cookie = 'RAG-XSRF-TOKEN=; Max-Age=0; path=/'
})

async function openChat(value: Record<string, unknown>) {
  setActivePinia(createPinia())
  document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
  const fetch = vi.fn((url: string, init?: RequestInit) => {
    if (url === '/openapi/v1.json') return Promise.resolve(json(doc))
    if (url === '/api/gateway/models') return Promise.resolve(json([{ name: 'fors/chat-eu', kind: 'model', modelKind: 'chat', residencies: ['Eu'], available: true }]))
    if (init?.method === 'PUT') return Promise.resolve(json({ value: {}, version: '2' }))
    return Promise.resolve(json({ value, version: '1' }))
  })
  vi.stubGlobal('fetch', fetch)
  const wrapper = mount(SettingsSectionForm, { props: { section: 'chat', label: PLATFORM.settingsSections.chat }, attachTo: document.body })
  await flushPromises()
  return { wrapper, fetch }
}

async function submit(fetch: ReturnType<typeof vi.fn>) {
  document.body.querySelector<HTMLFormElement>('form#settings-chat')!.dispatchEvent(new Event('submit'))
  await flushPromises()
  const put = fetch.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'PUT')!
  return JSON.parse(String((put[1] as RequestInit).body)) as { value: Record<string, unknown> }
}

describe('Settings, chat: the platform answer ceiling (Decisions "Answer length from the model", admin design §4.5)', () => {
  it('edits maxAnswerTokens as a field of its own and saves it in the section', async () => {
    const { wrapper, fetch } = await openChat({ condenseModel: 'fors/chat-eu', allowedLinkHosts: ['umea.se'] })
    const ceiling = document.body.querySelector<HTMLInputElement>('input#settings-chat-maxAnswerTokens')!
    expect(ceiling.value).toBe('')
    expect(document.body.textContent).toContain(PLATFORM.fields.maxAnswerTokens)
    expect(document.body.textContent).toContain(PLATFORM.fieldHints.maxAnswerTokens)
    ceiling.value = '4000'
    ceiling.dispatchEvent(new Event('input'))

    const body = await submit(fetch)
    expect(body.value).toEqual({ condenseModel: 'fors/chat-eu', allowedLinkHosts: ['umea.se'], maxAnswerTokens: 4000 })
    wrapper.unmount()
  })

  it('emptied, the ceiling is left out of the section', async () => {
    const { wrapper, fetch } = await openChat({ condenseModel: 'fors/chat-eu', maxAnswerTokens: 4000 })
    const ceiling = document.body.querySelector<HTMLInputElement>('input#settings-chat-maxAnswerTokens')!
    expect(ceiling.value).toBe('4000')
    ceiling.value = ''
    ceiling.dispatchEvent(new Event('input'))

    const body = await submit(fetch)
    expect(body.value).toEqual({ condenseModel: 'fors/chat-eu' })
    wrapper.unmount()
  })
})
