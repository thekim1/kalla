import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { useAuthStore } from '@/stores/auth'
import { bffUser, collection, department } from '@/test/fixtures'
import CollectionCreateDrawer from './CollectionCreateDrawer.vue'


describe('Create knowledge base (admin design §5.4 step 2)', () => {
  it('asks who may read the content, with no level preselected, and creates the collection', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser()
    const create = vi.spyOn(api.collections, 'create').mockResolvedValue(collection())
    const wrapper = mount(CollectionCreateDrawer, {
      props: { open: false, departments: [department({ id: 'd1', name: 'Stadsbyggnad' })] },
      attachTo: document.body,
    })
    await wrapper.setProps({ open: true })
    await flushPromises()
    const body = document.body

    expect(body.textContent).toContain('Who may read the content?')
    const radios = [...body.querySelectorAll<HTMLInputElement>('input[name=classification]')]
    expect(radios.map((r) => r.value)).toEqual(['public', 'internal', 'sensitive'])
    expect(radios.some((r) => r.checked)).toBe(false)

    // Saving without a name or a level says what is missing and sends nothing.
    body.querySelector<HTMLFormElement>('#collection-create')!.dispatchEvent(new Event('submit'))
    await flushPromises()
    expect(body.textContent).toContain('Enter a name.')
    expect(body.textContent).toContain('Choose who may read the content.')
    expect(create).not.toHaveBeenCalled()

    const name = body.querySelector<HTMLInputElement>('#collection-name')!
    name.value = 'Bygglov'
    name.dispatchEvent(new Event('input'))
    radios[1]!.checked = true
    radios[1]!.dispatchEvent(new Event('change'))
    body.querySelector<HTMLFormElement>('#collection-create')!.dispatchEvent(new Event('submit'))
    await flushPromises()

    // One department in scope: it is chosen for the user. No team field: the API picks the owner team. The contact is the
    // creator by default.
    expect(body.querySelector('#collection-team')).toBeNull()
    expect(create).toHaveBeenCalledWith({
      name: 'Bygglov', departmentId: 'd1', classification: 'internal', contactName: 'Anna Andersson', contactEmail: 'anna@example.invalid',
    })
    expect(wrapper.emitted('created')?.[0]?.[0]).toMatchObject({ id: 'c1' })
    wrapper.unmount()
  })
})
