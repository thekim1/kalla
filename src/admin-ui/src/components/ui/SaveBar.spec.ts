import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { createMemoryHistory, createRouter, RouterView } from 'vue-router'
import { useDirty } from '@/composables/useDirty'
import { useUnsavedGuard } from '@/composables/useUnsavedGuard'
import SaveBar from './SaveBar.vue'

afterEach(() => {
  vi.restoreAllMocks()
  document.body.innerHTML = ''
  document.documentElement.style.scrollPaddingBottom = ''
})

describe('SaveBar (admin design §5.12 rule 7, Saving)', () => {
  it('shows nothing without changes, then the count, the consequence and a polite announcement in a labelled region', async () => {
    HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
    const wrapper = mount(SaveBar, { props: { changes: 0 }, attachTo: document.body })
    expect(wrapper.find('[data-test=save-bar]').exists()).toBe(false)
    expect(wrapper.get('[data-test=save-bar-announcement]').attributes('aria-live')).toBe('polite')

    await wrapper.setProps({ changes: 3, consequence: 'Version 2 stays published until you publish the draft' })
    await flushPromises()
    const bar = wrapper.get('[data-test=save-bar]')
    expect(bar.element.tagName).toBe('SECTION')
    expect(bar.attributes('aria-label')).toBe('Unsaved changes')
    expect(wrapper.get('[data-test=save-bar-count]').text()).toBe('3 unsaved changes')
    expect(wrapper.get('[data-test=save-bar-consequence]').text()).toContain('Version 2 stays published until you publish the draft')
    expect(wrapper.get('[data-test=save-bar-announcement]').text()).toBe('3 unsaved changes')
    expect(wrapper.get('[data-test=save-bar-save]').text()).toBe('Save changes')

    await wrapper.setProps({ changes: 1, consequence: undefined })
    expect(wrapper.get('[data-test=save-bar-count]').text()).toBe('1 unsaved change')
    wrapper.unmount()
  })

  it('saves at once, and discards only after one confirmation', async () => {
    HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
    HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false }
    const wrapper = mount(SaveBar, { props: { changes: 2 }, attachTo: document.body })
    await wrapper.get('[data-test=save-bar-save]').trigger('click')
    expect(wrapper.emitted('save')).toHaveLength(1)

    await wrapper.get('[data-test=save-bar-discard]').trigger('click')
    await flushPromises()
    expect(wrapper.emitted('discard')).toBeUndefined()
    expect(document.body.textContent).toContain('Discard 2 unsaved changes?')
    const confirm = [...document.body.querySelectorAll('dialog button')].find((b) => b.textContent?.trim() === 'Discard') as HTMLButtonElement
    confirm.click()
    await flushPromises()
    expect(wrapper.emitted('discard')).toHaveLength(1)
    wrapper.unmount()
  })

  it('pads the page\'s scroll by its height while it shows, so a focused field is not hidden behind it', async () => {
    const wrapper = mount(SaveBar, { props: { changes: 0 }, attachTo: document.body })
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(72)
    await wrapper.setProps({ changes: 1 })
    await flushPromises()
    expect(document.documentElement.style.scrollPaddingBottom).toBe('72px')
    await wrapper.setProps({ changes: 0 })
    await flushPromises()
    expect(document.documentElement.style.scrollPaddingBottom).toBe('')
    wrapper.unmount()
  })
})

describe('useDirty and the leave guard', () => {
  it('counts the fields that differ from what was saved, nothing before the first reset', () => {
    const name = ref('Bygglov')
    const tags = ref(['a'])
    const dirty = useDirty(() => ({ name: name.value, tags: tags.value }))
    name.value = 'x'
    expect(dirty.changes.value).toBe(0)
    dirty.reset()
    name.value = 'Bygglov 2'
    tags.value = ['a', 'b']
    expect(dirty.changes.value).toBe(2)
    name.value = 'x'
    tags.value = ['a']
    expect([dirty.changes.value, dirty.dirty.value]).toEqual([0, false])
  })

  it('asks before a route change with unsaved changes, and stays when the answer is no', async () => {
    const dirty = ref(true)
    const Form = defineComponent({ setup: () => { useUnsavedGuard(() => dirty.value); return () => h('form') } })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/form', component: Form }, { path: '/other', component: { render: () => h('p') } }] })
    await router.push('/form')
    mount(RouterView, { global: { plugins: [router] } })
    await flushPromises()

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await router.push('/other')
    expect(confirm).toHaveBeenCalledWith('You have unsaved changes. Leave without saving them?')
    expect(router.currentRoute.value.path).toBe('/form')

    confirm.mockReturnValue(true)
    await router.push('/other')
    expect(router.currentRoute.value.path).toBe('/other')

    await router.push('/form')
    await flushPromises()
    dirty.value = false
    confirm.mockClear()
    await router.push('/other')
    expect(confirm).not.toHaveBeenCalled()
  })
})
