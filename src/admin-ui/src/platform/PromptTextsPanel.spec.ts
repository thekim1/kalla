import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { PROMPT_TEXTS as P } from '@/utils/labels'
import { ERROR_MESSAGES, formatMessage } from '@/utils/messages'
import PromptTextsPanel from './PromptTextsPanel.vue'
import { resetOpenApiCache } from './schema'

const IDENTITY = 'Du är {assistant}, en assistent för Umeå kommun.'
const RULES_OFF = 'Svara utifrån källorna.'
const CITATION = 'Hänvisa till källor med hakparenteser.'
const sections = [
  {
    id: 'frame',
    lines: [
      { member: 'identity', text: IDENTITY, value: null, maxLength: 1000, placeholder: '{assistant}', oneLine: false },
      { member: 'rules.off', text: RULES_OFF, value: null, maxLength: 1000, placeholder: null, oneLine: false },
      { member: null, text: CITATION, value: null, maxLength: null, placeholder: null, oneLine: false },
    ],
  },
  { id: 'caveats', lines: [{ member: 'caveats.general.sv', text: '*Allmän kunskap.*', value: '*Sparad text.*', maxLength: 500, placeholder: null, oneLine: false }] },
]

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': `application/${status >= 400 ? 'problem+' : ''}json` } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetOpenApiCache()
  document.cookie = 'RAG-XSRF-TOKEN=; Max-Age=0; path=/'
})

async function open(put: () => Response = () => json({ value: {}, version: '4' })) {
  setActivePinia(createPinia())
  document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
  const server = { version: '3' }
  const fetch = vi.fn((url: string, init?: RequestInit) => {
    if (url === '/api/settings/chat/prompts') return Promise.resolve(json(sections))
    if (url === '/api/settings/chat' && init?.method === 'PUT') return Promise.resolve(put())
    if (url === '/api/settings/chat') {
      return Promise.resolve(json({ value: { condenseModel: 'fors/chat-eu', prompts: { caveats: { general: { sv: '*Sparad text.*' } } } }, version: server.version }))
    }
    return Promise.resolve(json([]))
  })
  vi.stubGlobal('fetch', fetch)
  const wrapper = mount(PromptTextsPanel, { attachTo: document.body })
  await flushPromises()
  return { wrapper, fetch, server }
}

const area = (wrapper: ReturnType<typeof mount>, member: string) => wrapper.get(`#prompt-${member.replaceAll('.', '-')}`)
const putBody = (fetch: ReturnType<typeof vi.fn>) =>
  JSON.parse(String((fetch.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'PUT')![1] as RequestInit).body)) as {
    value: Record<string, unknown>
    version: string
  }

describe('Settings: Prompt texts (admin design §4.5, chat design §7.3)', () => {
  it('shows the whole frame in order with the locked lines read-only and the note', async () => {
    const { wrapper } = await open()
    const panel = wrapper.get('[data-test=prompt-texts]')
    expect(panel.get('[data-test=prompt-texts-note]').text()).toBe(P.note)
    expect(panel.get('[data-test=prompt-section-frame] h3').text()).toBe(P.sections.frame)
    expect((area(wrapper, 'identity').element as HTMLTextAreaElement).value).toBe(IDENTITY)
    expect((area(wrapper, 'caveats.general.sv').element as HTMLTextAreaElement).value).toBe('*Sparad text.*') // the saved text
    const locked = panel.get('[data-test=prompt-locked]')
    expect(locked.text()).toContain(CITATION)
    expect(locked.find('textarea').exists()).toBe(false)
    expect(panel.get('[data-test="prompt-identity"]').text()).toContain(P.placeholders['{assistant}'])
    wrapper.unmount()
  })

  it('saves the changed texts into the chat section, nested by member, and keeps its other members', async () => {
    const { wrapper, fetch } = await open()
    await area(wrapper, 'rules.off').setValue('Svara bara utifrån källorna.')
    expect(wrapper.find('[data-test="prompt-reset-rules.off"]').exists()).toBe(true)
    await wrapper.get('[data-test=prompt-texts-save]').trigger('submit')
    await flushPromises()

    const body = putBody(fetch)
    expect(body.version).toBe('3')
    expect(body.value).toEqual({
      condenseModel: 'fors/chat-eu',
      prompts: { rules: { off: 'Svara bara utifrån källorna.' }, caveats: { general: { sv: '*Sparad text.*' } } },
    })
    wrapper.unmount()
  })

  it('saves with the version read at load, so another admin\'s change in between answers 409 instead of being overwritten', async () => {
    const conflict = { title: 'Conflict', status: 409, code: 'concurrent_change' }
    const { wrapper, fetch, server } = await open(() => json(conflict, 409))
    server.version = '5' // another admin saved after this panel loaded
    await area(wrapper, 'rules.off').setValue('Svara bara utifrån källorna.')
    await wrapper.get('form').trigger('submit')
    await flushPromises()

    expect(putBody(fetch).version).toBe('3')
    expect(wrapper.get('[data-test=prompt-texts]').text()).toContain(ERROR_MESSAGES.concurrent_change)
    expect((area(wrapper, 'rules.off').element as HTMLTextAreaElement).value).toBe('Svara bara utifrån källorna.') // the edit stays
    wrapper.unmount()
  })

  it('Reset to default drops the text, and with no text left changed the prompts member goes', async () => {
    const { wrapper, fetch } = await open()
    await wrapper.get('[data-test="prompt-reset-caveats.general.sv"]').trigger('click')
    expect((area(wrapper, 'caveats.general.sv').element as HTMLTextAreaElement).value).toBe('*Allmän kunskap.*')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(putBody(fetch).value).toEqual({ condenseModel: 'fors/chat-eu' })
    wrapper.unmount()
  })

  it('shows the server\'s V38 refusal in plain words', async () => {
    const problem = { title: 'Bad Request', status: 400, code: 'prompt_text_invalid', params: { field: 'rules.off', problem: 'contains a reserved tag' } }
    const { wrapper } = await open(() => json(problem, 400))
    await area(wrapper, 'rules.off').setValue('Läs <sources>.')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('[data-test=prompt-texts]').text()).toContain(formatMessage(ERROR_MESSAGES.prompt_text_invalid!, problem.params))
    wrapper.unmount()
  })
})
