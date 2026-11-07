import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { api } from '@/api'
import { ApiError } from '@/api/client'
import { collection } from '@/test/fixtures'
import CollectionEditDrawer from './CollectionEditDrawer.vue'

async function open() {
  const wrapper = mount(CollectionEditDrawer, { props: { open: false, collection: collection({ classification: 'internal', version: 'v3' }) }, attachTo: document.body })
  await wrapper.setProps({ open: true })
  await flushPromises()
  return wrapper
}

function choose(level: string) {
  const radio = document.body.querySelector<HTMLInputElement>(`#collection-edit-level-${level}`)!
  radio.checked = true
  radio.dispatchEvent(new Event('change'))
}

function submit() {
  document.body.querySelector<HTMLFormElement>('#collection-edit')!.dispatchEvent(new Event('submit'))
}

describe('Edit knowledge base (admin design §5.5.2, #446)', () => {
  it('saves the level with the other own fields', async () => {
    const saved = collection({ classification: 'public', version: 'v4' })
    const update = vi.spyOn(api.collections, 'update').mockResolvedValue(saved)
    const wrapper = await open()
    const c = collection()

    expect(document.body.querySelector<HTMLInputElement>('#collection-edit-level-internal')!.checked).toBe(true)
    choose('public')
    submit()
    await flushPromises()

    expect(update).toHaveBeenCalledWith(c.id, {
      name: c.name, ownerTeamId: c.ownerTeamId, classification: 'public', contactName: c.contactName, contactEmail: c.contactEmail, version: 'v3',
    })
    expect(wrapper.emitted('saved')?.[0]?.[0]).toEqual(saved)
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    wrapper.unmount()
  })

  it('shows a refusal with the affected objects by name and stays open', async () => {
    vi.spyOn(api.collections, 'update').mockRejectedValue(new ApiError(409, 'PUT', {
      status: 409, code: 'assistant_below_collection', params: { collection: 'Bygglov', level: 'sensitive' },
      problems: [{ code: 'assistant_below_collection', params: { collection: 'Bygglov', level: 'sensitive' }, affected: ['Bygglovsassistenten', 'Kundtjänst'] }],
      affected: ['Bygglovsassistenten', 'Kundtjänst'],
    }))
    const wrapper = await open()
    choose('sensitive')
    submit()
    await flushPromises()

    const refusals = document.body.querySelector('[data-test=refusals]')!
    expect(refusals.textContent).toContain('Bygglovsassistenten, Kundtjänst')
    expect(wrapper.emitted('saved')).toBeUndefined()
    wrapper.unmount()
  })
})
