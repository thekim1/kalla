import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { PathRuleJson } from '@/api/types'
import TopicRuleForm from './TopicRuleForm.vue'

const sources = [{ id: 's1', name: 'umea.se' }, { id: 's2', name: 'Filer' }]
const preview = (matched: number) => ({ matched, total: 2323, checked: 2323, partial: false, sample: matched ? [{ documentId: 'd1', title: 'Bygglov', url: 'https://www.umea.se/bygga/bygglov/', displayName: null }] : [] })

function mountForm(initial: { sourceId: string; rule: PathRuleJson; tag: string } | null = null) {
  setActivePinia(createPinia())
  return mount(TopicRuleForm, { props: { open: true, collectionId: 'c1', sources, initial }, attachTo: document.body })
}

const q = <T extends Element = HTMLElement>(test: string) => document.body.querySelector<T>(`[data-test=${test}]`)

async function submit(): Promise<void> {
  q<HTMLFormElement>('topic-form')!.dispatchEvent(new Event('submit', { cancelable: true }))
  await flushPromises()
}

async function type(test: string, value: string): Promise<void> {
  const field = document.body.querySelector<HTMLInputElement>(`[data-test=${test}] input, input[data-test=${test}], [data-test=${test}] textarea`)!
  field.value = value
  field.dispatchEvent(new Event('input'))
  await flushPromises()
}

describe('The topic form (admin design §5.5.2; Decisions "Topics editor")', () => {
  it.each<[string, PathRuleJson]>([
    ['under the address', { match: 'https://www.umea.se/bygga/', on: 'path', tags: ['bygga'] }],
    ['whose title contains', { match: '(?i)Protokoll \\(2024\\)', on: 'title-regex', tags: ['protokoll'] }],
    ['of the file type', { match: 'application/pdf', on: 'mediaType', tags: ['pdf'] }],
    ['Advanced: a path expression with a date', { match: '^/protokoll/(?<y>\\d{4})/', on: 'path-regex', tags: ['protokoll'], date: '${y}-01-01' }],
    ['Advanced: a host with a document type', { match: 'www.umea.se', on: 'host', tags: ['umea'], documentType: 'page' }],
    ['as JSON: a rule with two topics', { match: '/kommun/', on: 'path', tags: ['kommun', 'politik'] }],
  ])('saves %s unchanged when it is opened and saved', async (_, rule) => {
    const previewSpy = vi.spyOn(api.collections, 'previewTopicRule').mockResolvedValue(preview(3))
    const wrapper = mountForm({ sourceId: 's2', rule, tag: rule.tags![0]! })
    await flushPromises()
    await submit()

    expect(previewSpy).toHaveBeenCalledWith('c1', { sourceId: 's2', rule }, expect.any(AbortSignal))
    expect(wrapper.emitted('save')).toEqual([[{ sourceId: 's2', rule }]])
    wrapper.unmount()
  })

  it('counts what a new rule matches, lists the first ones, and checks the topic as typed', async () => {
    const previewSpy = vi.spyOn(api.collections, 'previewTopicRule').mockResolvedValue(preview(212))
    const wrapper = mountForm()
    await flushPromises()
    expect(q<HTMLInputElement>('topic-kind-under')!.checked).toBe(true)

    await type('topic-form-address', '/bygga/')
    await new Promise((r) => setTimeout(r, 450)) // debounced
    await flushPromises()
    expect(previewSpy).toHaveBeenLastCalledWith('c1', { sourceId: 's1', rule: { match: '/bygga/', on: 'path', tags: ['preview'] } }, expect.any(AbortSignal))
    expect(q('topic-preview-count')!.textContent).toMatch(/^Matches 212 of 2\s323 documents$/)
    expect(document.body.querySelectorAll('[data-test=topic-preview-item]')).toHaveLength(1)

    await submit() // no topic yet
    expect(wrapper.emitted('save')).toBeUndefined()
    expect(document.body.textContent).toContain('Write a topic.')
    await type('topic-form-tag', 'Bygga Bo')
    expect(document.body.textContent).toContain('Use lower-case letters, digits and hyphens, at most 50.')
    await type('topic-form-tag', 'Bygga')
    await submit()
    expect(wrapper.emitted('save')).toEqual([[{ sourceId: 's1', rule: { match: '/bygga/', on: 'path', tags: ['bygga'] } }]])
    wrapper.unmount()
  })

  it('takes the first source when the sources arrive after the form opened', async () => {
    const previewSpy = vi.spyOn(api.collections, 'previewTopicRule').mockResolvedValue(preview(10))
    setActivePinia(createPinia())
    const wrapper = mount(TopicRuleForm, { props: { open: true, collectionId: 'c1', sources: [], initial: null }, attachTo: document.body })
    await flushPromises()
    q<HTMLInputElement>('topic-kind-type')!.click()
    await flushPromises()
    await wrapper.setProps({ sources })
    await new Promise((r) => setTimeout(r, 450))
    await flushPromises()
    expect(previewSpy).toHaveBeenLastCalledWith('c1', { sourceId: 's1', rule: { match: 'application/pdf', on: 'mediaType', tags: ['preview'] } }, expect.any(AbortSignal))
    expect(q('topic-preview-count')!.textContent).toMatch(/^Matches 10 of 2\s323 documents$/)
    wrapper.unmount()
  })

  it('writes the title as escaped text', async () => {
    vi.spyOn(api.collections, 'previewTopicRule').mockResolvedValue(preview(4))
    const wrapper = mountForm()
    await flushPromises()
    q<HTMLInputElement>('topic-kind-title')!.click()
    await flushPromises()
    await type('topic-form-title', 'Protokoll (KF)')
    await type('topic-form-tag', 'protokoll')
    await submit()
    expect(wrapper.emitted('save')).toEqual([[{ sourceId: 's1', rule: { match: '(?i)Protokoll \\(KF\\)', on: 'title-regex', tags: ['protokoll'] } }]])
    wrapper.unmount()
  })

  it('says when a rule matches nothing and asks again at Save, which still saves it', async () => {
    vi.spyOn(api.collections, 'previewTopicRule').mockResolvedValue(preview(0))
    const wrapper = mountForm()
    await flushPromises()
    await type('topic-form-address', '/nytt/')
    await type('topic-form-tag', 'nytt')
    await submit()

    expect(q('topic-preview-none')!.textContent).toBe('No document of umea.se matches this rule yet.')
    expect(wrapper.emitted('save')).toBeUndefined()
    expect(document.body.textContent).toContain('Save a rule that matches nothing?')
    q<HTMLButtonElement>('confirm')!.click()
    await flushPromises()
    expect(wrapper.emitted('save')).toEqual([[{ sourceId: 's1', rule: { match: '/nytt/', on: 'path', tags: ['nytt'] } }]])
    wrapper.unmount()
  })

  it('shows the server refusal of an invalid expression and saves nothing', async () => {
    const { ApiError } = await import('@/api/client')
    vi.spyOn(api.collections, 'previewTopicRule').mockRejectedValue(
      new ApiError(400, 'POST', { title: 'x', status: 400, errors: { rule: ['The setting pathRules is not valid for this processing: invalid regex'] } } as never),
    )
    const wrapper = mountForm({ sourceId: 's1', rule: { match: '(', on: 'path-regex', tags: ['x'] }, tag: 'x' })
    await flushPromises()
    await submit()
    expect(q('topic-preview-error')).not.toBeNull()
    expect(wrapper.emitted('save')).toBeUndefined()
    wrapper.unmount()
  })

  it('asks a rule written as JSON for valid topics, and shows a refused plan without closing (#1076)', async () => {
    vi.spyOn(api.collections, 'previewTopicRule').mockResolvedValue(preview(3))
    const wrapper = mountForm({ sourceId: 's1', rule: { match: '/kommun/', on: 'path', tags: ['kommun', 'politik'] }, tag: 'kommun' })
    await flushPromises()
    await type('topic-form-json', JSON.stringify({ match: '/kommun/', on: 'path', tags: [] }))
    await submit()
    expect(wrapper.emitted('save')).toBeUndefined()
    expect(document.body.textContent).toContain('Give the rule at least one topic in "tags".')
    await type('topic-form-json', JSON.stringify({ match: '/kommun/', on: 'path', tags: ['a b'] }))
    await submit()
    expect(wrapper.emitted('save')).toBeUndefined()

    await type('topic-form-json', JSON.stringify({ match: '/kommun/', on: 'path', tags: ['kommun'] }))
    await submit()
    expect(wrapper.emitted('save')).toHaveLength(1)
    expect(wrapper.emitted('update:open')).toBeUndefined() // the parent closes it once the plan is under way
    await wrapper.setProps({ saveError: 'The processing has changed.' })
    expect(q('topic-form-error')!.textContent).toContain('The processing has changed.')
    wrapper.unmount()
  })

  it('aborts the preview it no longer needs, and says when the count stopped at the time budget (#1076)', async () => {
    const signals: AbortSignal[] = []
    vi.spyOn(api.collections, 'previewTopicRule').mockImplementation(async (_id, _body, signal) => {
      signals.push(signal!)
      return { ...preview(0), checked: 800, partial: true }
    })
    vi.useFakeTimers()
    const wrapper = mountForm()
    await type('topic-form-address', '/a/')
    await vi.advanceTimersByTimeAsync(450)
    await type('topic-form-address', '/ab/')
    await vi.advanceTimersByTimeAsync(450)
    vi.useRealTimers()
    await flushPromises()
    expect(signals).toHaveLength(2)
    expect(signals[0]!.aborted).toBe(true)
    expect(signals[1]!.aborted).toBe(false)
    expect(q('topic-preview-count')!.textContent).toContain('Matches 0 of the first 800 of 2')
    expect(q('topic-preview-none')).toBeNull() // not "matches nothing": it wasn't all checked
    wrapper.unmount()
  })
})
