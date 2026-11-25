import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, RouterLinkStub } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import type { Assistant, AttentionItem } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { bffUser } from '@/test/fixtures'
import { ATTENTION } from '@/utils/labels'
import { attentionLink } from './attention'
import NeedsAttention from './NeedsAttention.vue'

const unused: AttentionItem = {
  kind: 'assistant_unused', objectType: 'assistant', objectId: 'a1', objectName: 'Bygglovsguiden', departmentId: 'd1', since: '2026-07-01T00:00:00Z',
  details: { days: '90', lastUsed: '2026-07-01' },
}

function setup(roles: string[]) {
  setActivePinia(createPinia())
  const auth = useAuthStore()
  auth.user = bffUser({ roles })
  auth.status = 'authenticated'
  vi.spyOn(api.assistants, 'get').mockResolvedValue({ id: 'a1', version: '42' } as Assistant)
  return mount(NeedsAttention, { props: { items: [unused] }, attachTo: document.body, global: { stubs: { RouterLink: RouterLinkStub } } })
}

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('the unused-assistant attention item (admin design §5.5.1, B-68)', () => {
  it('says how long and when it was last asked, and opens the assistant', () => {
    expect(ATTENTION.kinds.assistant_unused!({ days: '90', lastUsed: '1 Jul 2026' }, 'Bygglovsguiden')).toBe('No questions in 90 days: Bygglovsguiden, last asked 1 Jul 2026')
    expect(ATTENTION.kinds.assistant_unused!({ days: '90' }, 'Bygglovsguiden')).toBe('No questions in 90 days: Bygglovsguiden, never asked')
    expect(attentionLink(unused)).toEqual({ name: 'assistant', params: { id: 'a1' } })
  })

  it('keeps it with the assistant\'s version, says so and asks Home to read the list again', async () => {
    const wrapper = setup(['rag-editor'])
    const keep = vi.spyOn(api.assistants, 'keep').mockResolvedValue({} as Assistant)
    expect(wrapper.get('[data-test=attention-item]').text()).toContain('last asked 1 Jul 2026')
    expect(wrapper.get('[data-test=attention-keep]').attributes('aria-label')).toBe(ATTENTION.keepFor('Bygglovsguiden'))
    await wrapper.get('[data-test=attention-keep]').trigger('click')
    await flushPromises()
    expect(keep).toHaveBeenCalledWith('a1', '42')
    expect(useUiStore().toasts.at(-1)?.message).toBe(ATTENTION.kept('Bygglovsguiden'))
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })

  it('archives it only after the confirmation', async () => {
    const wrapper = setup(['rag-editor'])
    const archive = vi.spyOn(api.assistants, 'archive').mockResolvedValue({} as Assistant)
    await wrapper.get('[data-test=attention-archive]').trigger('click')
    expect(archive).not.toHaveBeenCalled()
    document.querySelector<HTMLButtonElement>('[data-test=confirm]')!.click()
    await flushPromises()
    expect(archive).toHaveBeenCalledWith('a1', '42')
    expect(wrapper.emitted('changed')).toHaveLength(1)
  })

  it('offers neither to a reader', () => {
    const wrapper = setup(['rag-viewer'])
    expect(wrapper.find('[data-test=attention-keep]').exists()).toBe(false)
    expect(wrapper.find('[data-test=attention-archive]').exists()).toBe(false)
    expect(wrapper.find('[data-test=attention-link]').exists()).toBe(true)
  })
})
