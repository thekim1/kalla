import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { openMenu } from '@/test/helpers'
import UiDialog from './UiDialog.vue'
import MenuButton, { type MenuItem } from './MenuButton.vue'

const items: MenuItem[] = [
  { key: 'edit', label: 'Edit name and contact' },
  { key: 'move', label: 'Change department', disabled: true },
  { key: 'copy', label: 'Copy' },
  { key: 'delete', label: 'Delete…', danger: true },
]

function phone(on: boolean): void {
  vi.stubGlobal('matchMedia', (query: string) => ({ matches: on && query.includes('max-width'), addEventListener: () => undefined, removeEventListener: () => undefined }))
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('MenuButton (admin design §5.12 rule 1 and its accessibility table)', () => {
  it('is a menu button named for its object, closed until opened', () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions for Demo-assistent', items }, attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]')
    expect(button.element.tagName).toBe('BUTTON')
    expect(button.attributes('aria-haspopup')).toBe('menu')
    expect(button.attributes('aria-expanded')).toBe('false')
    expect(button.attributes('aria-label')).toBe('More actions for Demo-assistent')
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    wrapper.unmount()
  })

  it('opens on the first item, moves with the arrows, Home and End over a disabled item too, and Esc returns focus to the button', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]')
    await button.trigger('click')
    await flushPromises()
    expect(button.attributes('aria-expanded')).toBe('true')
    const menu = openMenu()
    expect(button.attributes('aria-controls')).toBe(menu.attributes('id'))
    expect(document.activeElement?.textContent).toBe('Edit name and contact')

    await menu.trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement?.textContent).toBe('Change department') // APG keeps a disabled item focusable
    await menu.trigger('keydown', { key: 'End' })
    expect(document.activeElement?.textContent).toBe('Delete…')
    await menu.trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement?.textContent).toBe('Edit name and contact') // wraps
    await menu.trigger('keydown', { key: 'ArrowUp' })
    expect(document.activeElement?.textContent).toBe('Delete…')

    await menu.trigger('keydown', { key: 'Escape' })
    await flushPromises()
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    expect(document.activeElement).toBe(button.element)
    wrapper.unmount()
  })

  it('opens on the last item with ArrowUp, acts on Enter (a click) and emits the key, but not for a disabled item', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('keydown', { key: 'ArrowUp' })
    await flushPromises()
    expect(document.activeElement?.textContent).toBe('Delete…')
    expect(openMenu().get('[data-test=menu-delete]').classes()).toContain('text-danger')

    await openMenu().get('[data-test=menu-move]').trigger('click')
    expect(wrapper.emitted('select')).toBeUndefined()
    expect(openMenu().get('[data-test=menu-move]').attributes('aria-disabled')).toBe('true')

    await openMenu().get('[data-test=menu-copy]').trigger('click')
    expect(wrapper.emitted('select')).toEqual([['copy']])
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    wrapper.unmount()
  })

  it('closes on Tab with focus on the button (Tab goes on from there), and on a click outside', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await openMenu().trigger('keydown', { key: 'Tab' })
    await flushPromises()
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    expect(document.activeElement).toBe(wrapper.get('[data-test=menu-button]').element)

    await wrapper.get('[data-test=menu-button]').trigger('click')
    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    wrapper.unmount()
  })

  it('returns focus to the ⋯ button when a dialog opened from an item closes (WCAG 2.4.3)', async () => {
    phone(false)
    HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
    HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false }
    const open = ref(false)
    const Page = defineComponent({
      setup: () => () => [
        h(MenuButton, { label: 'More actions', items, onSelect: () => (open.value = true) }),
        h(UiDialog, { open: open.value, title: 'Edit', 'onUpdate:open': (v: boolean) => (open.value = v) }, () => h('input', { 'data-test': 'field' })),
      ],
    })
    const wrapper = mount(Page, { attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]').element
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    ;(document.body.querySelector('[data-test=menu-edit]') as HTMLButtonElement).click()
    await flushPromises()
    expect(document.body.querySelector('dialog')?.open).toBe(true)
    open.value = false
    await flushPromises()
    expect(document.activeElement).toBe(button)
    wrapper.unmount()
  })

  it('is teleported to the body with fixed coordinates, and opens upwards when there is no room below', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]').element as HTMLElement
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(160)
    const rect = (top: number) => ({ top, bottom: top + 32, left: 900, right: 932, width: 32, height: 32, x: 900, y: top, toJSON: () => ({}) })

    button.getBoundingClientRect = () => rect(100)
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role=menu]').exists()).toBe(false) // not inside the page's own markup
    expect(openMenu().classes()).toContain('fixed')
    expect((openMenu().element as HTMLElement).style.top).toBe('136px')
    await openMenu().trigger('keydown', { key: 'Escape' })

    button.getBoundingClientRect = () => rect(window.innerHeight - 40)
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    const style = (openMenu().element as HTMLElement).style
    expect([style.top, style.bottom]).toEqual(['', '44px'])

    // A scroll moves the button: the menu stays open and follows it.
    button.getBoundingClientRect = () => rect(200)
    window.dispatchEvent(new Event('scroll'))
    await flushPromises()
    expect((openMenu().element as HTMLElement).style.top).toBe('236px')
    wrapper.unmount()
  })

  it('is a bottom sheet on a phone: a modal dialog titled by the name, every item (a disabled one shown as such) and Close', async () => {
    phone(true)
    HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
    HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false }
    const withCompare = [{ key: 'compare', label: 'Compare with published', phoneOnly: true }, ...items]
    const wrapper = mount(MenuButton, { props: { label: 'More actions for Bygglov', items: withCompare }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    const sheet = document.body.querySelector('dialog')!
    expect(sheet.textContent).toContain('More actions for Bygglov')
    const labels = [...sheet.querySelectorAll('[data-test=menu-sheet] button')].map((b) => b.textContent)
    expect(labels).toEqual(['Compare with published', 'Edit name and contact', 'Change department', 'Copy', 'Delete…'])
    expect(sheet.querySelector('[data-test=menu-move]')?.getAttribute('aria-disabled')).toBe('true')
    ;(sheet.querySelector('[data-test=menu-move]') as HTMLButtonElement).click()
    expect(wrapper.emitted('select')).toBeUndefined()
    ;(sheet.querySelector('[data-test=menu-copy]') as HTMLButtonElement).click()
    await flushPromises()
    expect(wrapper.emitted('select')).toEqual([['copy']])
    wrapper.unmount()
  })

  it('leaves a phone-only item out on a wide screen, and shows no button when nothing else is left', async () => {
    phone(false)
    const only = mount(MenuButton, { props: { label: 'More actions', items: [{ key: 'compare', label: 'Compare with published', phoneOnly: true }] }, attachTo: document.body })
    expect(only.find('[data-test=menu-button]').exists()).toBe(false)
    only.unmount()
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items: [{ key: 'compare', label: 'Compare', phoneOnly: true }, ...items] }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    expect(openMenu().find('[data-test=menu-compare]').exists()).toBe(false)
    wrapper.unmount()
  })
})
