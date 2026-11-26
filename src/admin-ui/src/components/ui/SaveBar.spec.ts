/* eslint-disable vue/one-component-per-file -- small host components for the guard and the bar, one per case */
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

describe('SaveBar (layout rule 7)', () => {
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

  /** The bar's computed position: `fixed` on a phone, `sticky` wider (no stylesheet in jsdom). */
  function position(value: 'fixed' | 'sticky'): void {
    const real = window.getComputedStyle.bind(window)
    vi.spyOn(window, 'getComputedStyle').mockImplementation((el, pseudo) => ({ ...real(el, pseudo), position: value }) as CSSStyleDeclaration)
  }

  it('pads the scroll by what it covers on a phone: its height and the bottom menu under it', async () => {
    position('fixed')
    const wrapper = mount(SaveBar, { props: { changes: 0 }, attachTo: document.body })
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(120)
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({
      top: window.innerHeight - 184, bottom: window.innerHeight - 64, left: 0, right: 390, width: 390, height: 120, x: 0, y: window.innerHeight - 184, toJSON: () => ({}),
    })
    await wrapper.setProps({ changes: 1 })
    await flushPromises()
    expect(document.documentElement.style.scrollPaddingBottom).toBe('184px')
    wrapper.unmount()
  })

  it('pads the scroll by its own height on a wider screen, where it is sticky and may sit mid-window at the form\'s end', async () => {
    position('sticky')
    const wrapper = mount(SaveBar, { props: { changes: 0 }, attachTo: document.body })
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(72)
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({
      top: 500, bottom: 572, left: 0, right: 680, width: 680, height: 72, x: 0, y: 500, toJSON: () => ({}),
    })
    await wrapper.setProps({ changes: 1 })
    await flushPromises()
    expect(document.documentElement.style.scrollPaddingBottom).toBe('72px') // not innerHeight - 500
    wrapper.unmount()
  })

  it('pads the scroll by the phone\'s strip while nothing is unsaved, so a focused field is not hidden behind it', async () => {
    position('fixed')
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(68)
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({
      top: window.innerHeight - 132, bottom: window.innerHeight - 64, left: 0, right: 390, width: 390, height: 68, x: 0, y: window.innerHeight - 132, toJSON: () => ({}),
    })
    const wrapper = mount(SaveBar, { props: { changes: 0 }, slots: { phone: '<button>Try the draft</button>' }, attachTo: document.body })
    await flushPromises()
    expect(document.documentElement.style.scrollPaddingBottom).toBe('132px')
    wrapper.unmount()
    expect(document.documentElement.style.scrollPaddingBottom).toBe('')
  })

  it('scrolls the field being edited clear of it when it appears over that field', async () => {
    const changes = ref(0)
    const Form = defineComponent({ setup: () => () => h('form', [h('textarea', { id: 'prompt' }), h(SaveBar, { changes: changes.value })]) })
    const wrapper = mount(Form, { attachTo: document.body })
    const field = wrapper.get('#prompt').element as HTMLTextAreaElement
    field.focus()
    const scroll = vi.fn()
    field.scrollIntoView = scroll
    field.getBoundingClientRect = () => ({ top: 500, bottom: 700, left: 0, right: 300, width: 300, height: 200, x: 0, y: 500, toJSON: () => ({}) })
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ top: 600, bottom: 680, left: 0, right: 300, width: 300, height: 80, x: 0, y: 600, toJSON: () => ({}) })
    changes.value = 1
    await flushPromises()
    expect(scroll).toHaveBeenCalledWith({ block: 'nearest' })
    wrapper.unmount()
  })

  it('keeps a spacer of its height under the form while it shows, for the phone where it is fixed', async () => {
    const wrapper = mount(SaveBar, { props: { changes: 0 }, attachTo: document.body })
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(120)
    expect(wrapper.find('[data-test=save-bar-spacer]').exists()).toBe(false)
    await wrapper.setProps({ changes: 2 })
    await flushPromises()
    const spacer = wrapper.get('[data-test=save-bar-spacer]')
    expect(spacer.attributes('style')).toContain('height: 120px')
    expect(spacer.classes()).toContain('md:hidden')
    wrapper.unmount()
  })

  it('holds the page\'s phone action: on its own while nothing is unsaved (not the Unsaved changes region), beside the buttons after', async () => {
    const wrapper = mount(SaveBar, { props: { changes: 0 }, slots: { phone: '<button data-test="try">Try the draft</button>' }, attachTo: document.body })
    expect(wrapper.find('[data-test=save-bar]').exists()).toBe(false)
    const idle = wrapper.get('[data-test=save-bar-phone]')
    expect(idle.classes()).toContain('md:hidden')
    expect(idle.find('[data-test=try]').exists()).toBe(true)
    expect(wrapper.get('[data-test=save-bar-spacer]').attributes('style')).toContain('height: 68px')
    await wrapper.setProps({ changes: 1 })
    await flushPromises()
    expect(wrapper.find('[data-test=save-bar-phone]').exists()).toBe(false)
    expect(wrapper.get('[data-test=save-bar] [data-test=try]').element.parentElement?.className).toContain('md:hidden')
    wrapper.unmount()
  })

  it('returns focus to the field last edited when it goes with focus in it (after Save changes)', async () => {
    const changes = ref(0)
    const Form = defineComponent({
      setup: () => () => h('form', [h('input', { id: 'name' }), h(SaveBar, { changes: changes.value, onSave: () => (changes.value = 0) })]),
    })
    const wrapper = mount(Form, { attachTo: document.body })
    const field = wrapper.get('#name').element as HTMLInputElement
    field.focus()
    changes.value = 1
    await flushPromises()
    ;(wrapper.get('[data-test=save-bar-save]').element as HTMLButtonElement).focus()
    await wrapper.get('[data-test=save-bar-save]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-test=save-bar]').exists()).toBe(false)
    expect(document.activeElement).toBe(field)
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

  it('counts a group\'s fields one by one, a field the group lost included, and an array as one', () => {
    const advanced = ref<Record<string, string>>({ topK: '8', temperature: '' })
    const links = ref([{ id: 'c1' }])
    const dirty = useDirty(() => ({ advanced: advanced.value, links: links.value }))
    dirty.reset()
    advanced.value = { topK: '10', temperature: '0,2' }
    links.value = [{ id: 'c1' }, { id: 'c2' }]
    expect(dirty.changes.value).toBe(3)
    advanced.value = { topK: '8' }
    expect(dirty.changes.value).toBe(2) // temperature is gone, links still differ
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

  it('asks on the same page for another object (a copy\'s new id) and on a tab change by a link, not on the page\'s own switch', async () => {
    const dirty = ref(true)
    const tab = ref('draft')
    const tabConfirm = vi.fn().mockReturnValue(false)
    const Page = defineComponent({
      setup: () => {
        useUnsavedGuard(() => dirty.value, { current: () => tab.value, of: (r) => String(r.query.tab ?? 'draft'), confirm: tabConfirm })
        return () => h('form')
      },
    })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/a/:id', component: Page }] })
    await router.push('/a/1')
    mount(RouterView, { global: { plugins: [router] } })
    await flushPromises()

    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false)
    await router.push('/a/2')
    expect(confirm).toHaveBeenCalledWith('You have unsaved changes. Leave without saving them?')
    expect(router.currentRoute.value.path).toBe('/a/1')

    await router.push('/a/1?tab=storage') // a link to another tab
    expect(tabConfirm).toHaveBeenCalledTimes(1)
    expect(router.currentRoute.value.query.tab).toBeUndefined()

    tab.value = 'storage' // the page's own switch has asked and set the tab; its query write passes
    await router.replace('/a/1?tab=storage')
    expect(tabConfirm).toHaveBeenCalledTimes(1)
    expect(router.currentRoute.value.query.tab).toBe('storage')
  })

  it('has the browser ask before closing or reloading the tab only while there are unsaved changes', () => {
    const dirty = ref(false)
    const Form = defineComponent({ setup: () => { useUnsavedGuard(() => dirty.value); return () => h('form') } })
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: Form }] })
    const wrapper = mount(Form, { global: { plugins: [router] } })
    const unload = () => {
      const e = new Event('beforeunload', { cancelable: true })
      window.dispatchEvent(e)
      return e.defaultPrevented
    }
    expect(unload()).toBe(false)
    dirty.value = true
    expect(unload()).toBe(true)
    wrapper.unmount()
    expect(unload()).toBe(false)
  })
})
