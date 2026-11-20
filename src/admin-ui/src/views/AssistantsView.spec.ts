import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { AssistantSummary } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { assistantSummary, bffUser } from '@/test/fixtures'
import AssistantsView from './AssistantsView.vue'

const push = vi.fn()
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRouter: () => ({ push }) }))

const row = (id: string, generalKnowledge: AssistantSummary['generalKnowledge']): AssistantSummary => assistantSummary({
  id, slug: id, name: `Assistant ${id}`, published: generalKnowledge !== null, generalKnowledge,
  publishedVersion: generalKnowledge !== null ? 1 : null, publishedAt: generalKnowledge !== null ? '2026-11-01T09:00:00Z' : null,
})
const mountView = () => mount(AssistantsView, { global: { stubs: { RouterLink: RouterLinkStub } } })

describe('Assistants (admin design §5.3, §5.5.4)', () => {
  it('shows the General knowledge badge for a published version above off (P1b item 40)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ total: 3, items: [row('a1', 'open'), row('a2', 'off'), row('a3', null)] })

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.get('[data-test=general-knowledge-a1]').text()).toBe('General knowledge')
    expect(wrapper.find('[data-test=general-knowledge-a2]').exists()).toBe(false)
    expect(wrapper.find('[data-test=general-knowledge-a3]').exists()).toBe(false)
  })

  it('marks an archived assistant Archived, not Draft only, and lists it last', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ total: 3, items: [{ ...row('old', null), state: 'archived' }, row('a1', 'off'), row('a2', null)] })

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.get('[data-test=archived-old]').text()).toBe('Archived')
    expect(wrapper.findAll('tbody tr').map((r) => r.text())).toEqual([
      expect.stringContaining('Assistant a1'), expect.stringContaining('Assistant a2'), expect.stringContaining('Assistant old'),
    ])
    expect(wrapper.findAll('tbody tr').at(-1)!.text()).not.toContain('Draft only')
  })

  it('shows the knowledge bases, model, publication and questions of 7 days, and opens a row as a link (§5.5.4, §5.5.2)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-viewer'] })
    auth.status = 'authenticated'
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.assistants, 'list').mockResolvedValue({
      total: 2,
      items: [
        assistantSummary({
          id: 'a1', name: 'Bygglov', collections: [{ id: 'c1', name: 'Blanketter' }, { id: 'c2', name: 'umea.se' }], model: 'fors/chat-eu', publishedVersion: 3,
          publishedAt: '2026-11-10T09:00:00Z', questions7d: 1234,
        }),
        assistantSummary({ id: 'a2', name: 'Skola', published: false, publishedVersion: null, publishedAt: null, hasDraft: true }),
      ],
    })

    const wrapper = mountView()
    await flushPromises()

    expect(wrapper.findAll('th[scope=col]').map((h) => h.text())).toEqual(['Name', 'Information class', 'Knowledge bases', 'Model', 'Published', 'Questions, 7 days'])
    const [first, second] = wrapper.findAll('tbody tr')
    expect(first!.get('[data-test=collections-a1]').text()).toBe('Blanketter, umea.se')
    expect(first!.text()).toContain('fors/chat-eu')
    expect(first!.get('[data-test=published-a1]').text()).toContain('Version 3')
    expect(first!.get('[data-test=published-a1]').text()).toContain('10 Nov 2026')
    expect(first!.text()).toMatch(/1\s234/)
    expect(second!.text()).toContain('Draft only')
    expect(second!.text()).toContain('None')
    expect(second!.text()).toContain('Not chosen')

    expect(wrapper.getComponent(RouterLinkStub).props('to')).toEqual({ name: 'assistant', params: { id: 'a1' } })
    await second!.findAll('td')[1]!.trigger('click')
    expect(push).toHaveBeenCalledWith({ name: 'assistant', params: { id: 'a2' } })
  })
})
