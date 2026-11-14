import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { PII_RULES as P } from '@/utils/labels'
import PiiSettingsForm from './PiiSettingsForm.vue'

function json(body: unknown) {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })
}

afterEach(() => {
  vi.unstubAllGlobals()
  document.cookie = 'RAG-XSRF-TOKEN=; Max-Age=0; path=/'
})

function mountForm(initial: Record<string, unknown>) {
  document.cookie = 'RAG-XSRF-TOKEN=t; path=/'
  return mount(PiiSettingsForm, { props: { initial, url: '/api/settings/pii', submitLabel: 'Save' }, attachTo: document.body })
}

const flagOf = (wrapper: VueWrapper, type: string) => (wrapper.get(`#pii-flag-${type}`).element as HTMLSelectElement).value
const sent = (fetch: ReturnType<typeof vi.fn>) => JSON.parse(String((fetch.mock.calls[0]![1] as RequestInit).body)) as Record<string, unknown>

describe('Settings, Personal data: the flag rules as a table (admin design §5.10; processing design §9.4)', () => {
  it('shows the worker\'s defaults when no rules are stored, and sends every type with its rule', async () => {
    const fetch = vi.fn().mockResolvedValue(json({}))
    vi.stubGlobal('fetch', fetch)
    const wrapper = mountForm({ flagRules: {}, ownDomains: ['umea.se'], nerMinScore: 0.8, version: '3' })
    expect(wrapper.findAll('[data-test^=pii-rule-]').map((r) => r.get('th').text())).toEqual(
      ['personnummer', 'samordningsnummer', 'emailExternal', 'email', 'phone', 'name'].map((t) => P.types[t]),
    )
    expect(['personnummer', 'samordningsnummer', 'emailExternal', 'email', 'phone', 'name'].map((t) => flagOf(wrapper, t)))
      .toEqual(['always', 'always', 'threshold', 'never', 'never', 'never'])
    expect((wrapper.get('#pii-min-emailExternal').element as HTMLInputElement).value).toBe('1')
    // A From field only for a threshold.
    expect(wrapper.find('#pii-min-phone').exists()).toBe(false)
    expect(wrapper.get('[data-test=pii-list-like] [role=switch]').attributes('aria-checked')).toBe('true')
    expect((wrapper.get('#pii-minNames').element as HTMLInputElement).value).toBe('5')

    await wrapper.get('#pii-flag-phone').setValue('threshold')
    await wrapper.get('#pii-min-phone').setValue('3')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(sent(fetch)).toEqual({
      flagRules: {
        types: {
          personnummer: { flag: 'always', min: 1 }, samordningsnummer: { flag: 'always', min: 1 }, emailExternal: { flag: 'threshold', min: 1 },
          email: { flag: 'never', min: 1 }, phone: { flag: 'threshold', min: 3 }, name: { flag: 'never', min: 1 },
        },
        listLike: { minNames: 5, minContacts: 5 },
      },
      ownDomains: ['umea.se'], nerMinScore: 0.8, version: '3',
    })
  })

  it('reads stored rules as the worker does: a type they leave out never flags, and a null list rule is off', async () => {
    const fetch = vi.fn().mockResolvedValue(json({}))
    vi.stubGlobal('fetch', fetch)
    const wrapper = mountForm({ flagRules: { types: { personnummer: { flag: 'always' } }, listLike: null }, ownDomains: [], nerMinScore: 0.9, version: '4' })
    expect(flagOf(wrapper, 'personnummer')).toBe('always')
    expect(flagOf(wrapper, 'samordningsnummer')).toBe('never')
    expect(wrapper.get('[data-test=pii-list-like] [role=switch]').attributes('aria-checked')).toBe('false')
    expect(wrapper.find('#pii-minNames').exists()).toBe(false)
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect((sent(fetch).flagRules as { listLike: unknown }).listLike).toBeNull()
  })

  it('checks the numbers before saving, and shows the server\'s refusal of the rules', async () => {
    const fetch = vi.fn()
    vi.stubGlobal('fetch', fetch)
    const wrapper = mountForm({ flagRules: {}, ownDomains: [], nerMinScore: 0.8, version: '1' })
    await wrapper.get('#pii-min-emailExternal').setValue('0')
    await wrapper.get('#pii-nerMinScore').setValue('2')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(fetch).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain(P.wholeNumber)
    expect(wrapper.text()).toContain(P.scoreRange)
    wrapper.unmount()

    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(
      JSON.stringify({ status: 400, title: 'Check the highlighted fields.', errors: { FlagRules: ['Unknown personal-data type: passport.'] } }),
      { status: 400, headers: { 'Content-Type': 'application/problem+json' } },
    )))
    const again = mountForm({ flagRules: {}, ownDomains: [], nerMinScore: 0.8, version: '1' })
    await again.get('form').trigger('submit')
    await flushPromises()
    expect(again.get('[data-test=pii-rules]').text()).toContain('Unknown personal-data type: passport.')
  })
})
