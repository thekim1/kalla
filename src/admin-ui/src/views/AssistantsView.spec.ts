import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { AssistantSummary } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { bffUser } from '@/test/fixtures'
import AssistantsView from './AssistantsView.vue'

const push = vi.fn()
vi.mock('vue-router', async (original) => ({ ...(await original<typeof import('vue-router')>()), useRouter: () => ({ push }) }))

const row = (id: string, generalKnowledge: AssistantSummary['generalKnowledge']): AssistantSummary => ({
  id, slug: id, name: `Assistant ${id}`, departmentId: 'd1', classification: 'internal', state: 'active', published: generalKnowledge !== null, hasDraft: false,
  generalKnowledge,
})

describe('Assistants (admin design §5.3, §5.5.4)', () => {
  it('shows the General knowledge badge for a published version above off (P1b item 40)', async () => {
    setActivePinia(createPinia())
    const auth = useAuthStore()
    auth.user = bffUser({ roles: ['rag-editor'] })
    auth.status = 'authenticated'
    vi.spyOn(api.organisation, 'departments').mockResolvedValue([])
    vi.spyOn(api.assistants, 'list').mockResolvedValue({ total: 3, items: [row('a1', 'open'), row('a2', 'off'), row('a3', null)] })

    const wrapper = mount(AssistantsView)
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

    const wrapper = mount(AssistantsView)
    await flushPromises()

    expect(wrapper.get('[data-test=archived-old]').text()).toBe('Archived')
    expect(wrapper.findAll('tbody tr').map((r) => r.text())).toEqual([
      expect.stringContaining('Assistant a1'), expect.stringContaining('Assistant a2'), expect.stringContaining('Assistant old'),
    ])
    expect(wrapper.findAll('tbody tr').at(-1)!.text()).not.toContain('Draft only')
  })
})
