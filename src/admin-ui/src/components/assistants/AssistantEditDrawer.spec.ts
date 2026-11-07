import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import type { Assistant } from '@/api/types'
import { ASSISTANT } from '@/utils/labels'
import AssistantEditDrawer from './AssistantEditDrawer.vue'

const assistant: Assistant = {
  id: 'a1', slug: 'bygglov', name: 'Bygglovsassistenten', description: 'Svarar om bygglov.', contactName: 'Anna', contactEmail: 'anna@umea.se', departmentId: 'd1',
  ownerTeamId: 't1', classification: 'internal', historyMode: 'none', retentionDays: 90, state: 'active', publishedVersion: 2, generalKnowledge: 'fallback', rerankers: [],
  draft: null, audienceClientIds: [], updatedAt: '2026-11-01T10:00:00Z', updatedBy: null, version: 'a3',
}

async function open() {
  const wrapper = mount(AssistantEditDrawer, { props: { open: false, assistant }, attachTo: document.body })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

function choose(level: string) {
  const radio = document.body.querySelector<HTMLInputElement>(`#assistant-edit-level-${level}`)!
  radio.checked = true
  radio.dispatchEvent(new Event('change'))
}

function submit() {
  document.body.querySelector<HTMLFormElement>('#assistant-edit')!.dispatchEvent(new Event('submit'))
}

const body = (level: string, confirmGeneralKnowledge: boolean) => ({
  name: 'Bygglovsassistenten', ownerTeamId: 't1', classification: level, description: 'Svarar om bygglov.', contactName: 'Anna', contactEmail: 'anna@umea.se',
  version: 'a3', confirmGeneralKnowledge,
})

describe('Edit assistant (admin design §5.5.4, #446)', () => {
  it('saves the level with the other own fields', async () => {
    const update = vi.spyOn(api.assistants, 'update').mockResolvedValue({ ...assistant, classification: 'sensitive', version: 'a4' })
    const wrapper = await open()
    choose('sensitive')
    submit()
    await flushPromises()
    expect(update).toHaveBeenCalledWith('a1', body('sensitive', false))
    expect(wrapper.emitted('saved')?.[0]?.[0]).toMatchObject({ classification: 'sensitive' })
    wrapper.unmount()
  })

  it('asks the general knowledge confirmation on a change to public and resends with it (V36, #445)', async () => {
    const update = vi.spyOn(api.assistants, 'update')
      .mockRejectedValueOnce(new ApiError(409, 'PUT', { status: 409, code: 'general_knowledge_public_unconfirmed', params: { generalKnowledge: 'fallback' } }))
      .mockResolvedValueOnce({ ...assistant, classification: 'public', version: 'a4' })
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const wrapper = await open()
    choose('public')
    submit()
    await flushPromises()
    expect(confirm).toHaveBeenCalledWith(ASSISTANT.generalKnowledgeConfirm)
    expect(update).toHaveBeenNthCalledWith(1, 'a1', body('public', false))
    expect(update).toHaveBeenNthCalledWith(2, 'a1', body('public', true))
    expect(wrapper.emitted('saved')?.[0]?.[0]).toMatchObject({ classification: 'public' })
    confirm.mockRestore()
    wrapper.unmount()
  })

  it('lists every refusal of the re-checked version with its affected objects', async () => {
    vi.spyOn(api.assistants, 'update').mockRejectedValue(new ApiError(409, 'PUT', {
      status: 409, code: 'assistant_below_collection', params: { collection: 'Socialtjänst', level: 'sensitive' },
      problems: [
        { code: 'assistant_below_collection', params: { collection: 'Socialtjänst', level: 'sensitive' }, affected: ['Socialtjänst'] },
        { code: 'audience_above_ceiling', params: {}, affected: ['Open WebUI (pilot)'] },
      ],
    }))
    const wrapper = await open()
    choose('public')
    submit()
    await flushPromises()
    const items = [...document.body.querySelectorAll('[data-test=refusals] li')].map((li) => li.textContent)
    expect(items).toHaveLength(2)
    expect(items[0]).toContain('(Socialtjänst)')
    expect(items[1]).toContain('(Open WebUI (pilot))')
    expect(wrapper.emitted('saved')).toBeUndefined()
    wrapper.unmount()
  })
})
