import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { api } from '@/api'
import { ApiError } from '@/api/client'
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
    expect(body.textContent).toContain('Choose what goes in.')
    expect(create).not.toHaveBeenCalled()

    const name = body.querySelector<HTMLInputElement>('#collection-name')!
    name.value = 'Bygglov'
    name.dispatchEvent(new Event('input'))
    radios[1]!.checked = true
    radios[1]!.dispatchEvent(new Event('change'))
    const web = body.querySelector<HTMLInputElement>('#collection-content-web')!
    web.checked = true
    web.dispatchEvent(new Event('change'))
    const addSource = vi.spyOn(api.collections, 'addSource')
    body.querySelector<HTMLFormElement>('#collection-create')!.dispatchEvent(new Event('submit'))
    await flushPromises()

    // One department in scope: it is chosen for the user. No team field: the API picks the owner team. The contact is the
    // creator by default.
    expect(body.querySelector('#collection-team')).toBeNull()
    expect(create).toHaveBeenCalledWith({
      name: 'Bygglov', departmentId: 'd1', classification: 'internal', contactName: 'Anna Andersson', contactEmail: 'anna@example.invalid',
    })
    expect(wrapper.emitted('created')?.[0]).toEqual([expect.objectContaining({ id: 'c1' }), 'web', null])
    expect(addSource).not.toHaveBeenCalled() // a website: the source drawer comes later, as before
    wrapper.unmount()
  })

  async function createWithFiles(level: 'public' | 'internal') {
    const wrapper = mount(CollectionCreateDrawer, {
      props: { open: false, departments: [department({ id: 'd1', name: 'Stadsbyggnad' })] },
      attachTo: document.body,
    })
    await wrapper.setProps({ open: true })
    await flushPromises()
    const body = document.body
    const name = body.querySelector<HTMLInputElement>('#collection-name')!
    name.value = 'Riktlinjer'
    name.dispatchEvent(new Event('input'))
    for (const id of [`collection-level-${level}`, 'collection-content-files']) {
      const radio = body.querySelector<HTMLInputElement>(`#${id}`)!
      radio.checked = true
      radio.dispatchEvent(new Event('change'))
    }
    body.querySelector<HTMLFormElement>('#collection-create')!.dispatchEvent(new Event('submit'))
    await flushPromises()
    return wrapper
  }

  it('creates the knowledge base and its upload source named Files in one submit (§5.5.7)', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser()
    vi.spyOn(api.collections, 'create').mockResolvedValue(collection({ id: 'c7', classification: 'public' }))
    const addSource = vi.spyOn(api.collections, 'addSource').mockResolvedValue({ id: 's7' } as never)
    const wrapper = await createWithFiles('public')

    // The collection's level; a public one gets the source drawer's default personal-data mode.
    expect(addSource).toHaveBeenCalledWith('c7', { type: 'upload', name: 'Files', classification: 'public', settings: { piiMode: 'hold' } })
    expect(wrapper.emitted('created')?.[0]).toEqual([expect.objectContaining({ id: 'c7' }), 'files', null])
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    wrapper.unmount()
  })

  it('still reports the knowledge base as created when its upload source fails', async () => {
    setActivePinia(createPinia())
    useAuthStore().user = bffUser()
    vi.spyOn(api.collections, 'create').mockResolvedValue(collection({ id: 'c8', classification: 'internal' }))
    const failure = new ApiError(500, 'POST', { code: 'internal' })
    const addSource = vi.spyOn(api.collections, 'addSource').mockRejectedValue(failure)
    const wrapper = await createWithFiles('internal')

    expect(addSource).toHaveBeenCalledWith('c8', { type: 'upload', name: 'Files', classification: 'internal', settings: {} })
    expect(wrapper.emitted('created')?.[0]).toEqual([expect.objectContaining({ id: 'c8' }), 'files', failure])
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    wrapper.unmount()
  })
})
