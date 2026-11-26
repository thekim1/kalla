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

function dialogs(): void {
  HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
  HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false }
}

const text = () => document.activeElement?.textContent?.trim()

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  document.body.innerHTML = ''
})

describe('MenuButton (APG menu button)', () => {
  it('is a menu button named for what it is, closed until opened', () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions for Demo', items }, attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]')
    expect(button.element.tagName).toBe('BUTTON')
    expect(button.attributes('aria-haspopup')).toBe('menu')
    expect(button.attributes('aria-expanded')).toBe('false')
    expect(button.attributes('aria-label')).toBe('More actions for Demo')
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
    expect(menu.attributes('aria-label')).toBe('More actions')
    expect(button.attributes('aria-controls')).toBe(menu.attributes('id'))
    expect(text()).toBe('Edit name and contact')

    await menu.trigger('keydown', { key: 'ArrowDown' })
    expect(text()).toBe('Change department') // APG keeps a disabled item focusable
    await menu.trigger('keydown', { key: 'End' })
    expect(text()).toBe('Delete…')
    await menu.trigger('keydown', { key: 'ArrowDown' })
    expect(text()).toBe('Edit name and contact') // wraps
    await menu.trigger('keydown', { key: 'ArrowUp' })
    expect(text()).toBe('Delete…')
    await menu.trigger('keydown', { key: 'Home' })
    expect(text()).toBe('Edit name and contact')

    await menu.trigger('keydown', { key: 'Escape' })
    await flushPromises()
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    expect(button.attributes('aria-expanded')).toBe('false')
    expect(document.activeElement).toBe(button.element)
    wrapper.unmount()
  })

  it('opens on the last item with ArrowUp, acts on Enter (a click) and emits the key, but not for a disabled item', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('keydown', { key: 'ArrowUp' })
    await flushPromises()
    expect(text()).toBe('Delete…')
    expect(openMenu().get('[data-test=menu-delete]').classes()).toContain('text-danger')

    await openMenu().get('[data-test=menu-move]').trigger('click')
    expect(wrapper.emitted('select')).toBeUndefined()
    expect(openMenu().get('[data-test=menu-move]').attributes('aria-disabled')).toBe('true')

    await openMenu().get('[data-test=menu-copy]').trigger('click')
    expect(wrapper.emitted('select')).toEqual([['copy']])
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    expect(document.activeElement).toBe(wrapper.get('[data-test=menu-button]').element)
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

  it('returns focus to the button when a dialog opened from an item closes (WCAG 2.4.3)', async () => {
    phone(false)
    dialogs()
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

  it('is teleported to the body with fixed coordinates, opens upwards when there is no room below, and follows a scroll', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]').element as HTMLElement
    vi.spyOn(HTMLElement.prototype, 'offsetHeight', 'get').mockReturnValue(160)
    const rect = (top: number) => ({ top, bottom: top + 32, left: 900, right: 932, width: 32, height: 32, x: 900, y: top, toJSON: () => ({}) })

    button.getBoundingClientRect = () => rect(100)
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role=menu]').exists()).toBe(false) // not inside the component's own markup
    expect(openMenu().classes()).toContain('fixed')
    expect((openMenu().element as HTMLElement).style.top).toBe('136px')
    expect((openMenu().element as HTMLElement).style.right).not.toBe('')
    await openMenu().trigger('keydown', { key: 'Escape' })

    button.getBoundingClientRect = () => rect(window.innerHeight - 40)
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    const style = (openMenu().element as HTMLElement).style
    expect([style.top, style.bottom]).toEqual(['', '44px'])

    button.getBoundingClientRect = () => rect(200)
    window.dispatchEvent(new Event('scroll'))
    await flushPromises()
    expect((openMenu().element as HTMLElement).style.top).toBe('236px')
    wrapper.unmount()
  })

  it('lines up with the button’s left edge with align="start"', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items, align: 'start' }, attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]').element as HTMLElement
    button.getBoundingClientRect = () => ({ top: 100, bottom: 132, left: 16, right: 56, width: 40, height: 32, x: 16, y: 100, toJSON: () => ({}) })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    const style = (openMenu().element as HTMLElement).style
    expect([style.left, style.right]).toEqual(['16px', ''])
    wrapper.unmount()
  })

  it('shows a group under its name as menuitemradio items with aria-checked, apart from the other items', async () => {
    phone(false)
    const grouped: MenuItem[] = [
      { key: 'a', label: 'Alpha', group: 'Pick', checked: true },
      { key: 'b', label: 'Beta', group: 'Pick', checked: false },
      { key: 'go', label: 'Go' },
    ]
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items: grouped }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    const group = openMenu().get('[role=group]')
    expect(document.getElementById(group.attributes('aria-labelledby')!)?.textContent).toBe('Pick')
    expect(group.findAll('[role=menuitemradio]').map((i) => [i.text(), i.attributes('aria-checked')])).toEqual([
      ['Alphacheck', 'true'],
      ['Beta', 'false'],
    ])
    expect(openMenu().find('[role=separator]').exists()).toBe(true)
    expect(openMenu().get('[data-test=menu-go]').attributes('role')).toBe('menuitem')
    await openMenu().trigger('keydown', { key: 'End' })
    expect(text()).toBe('Go') // arrows move through the group and the rest alike
    await openMenu().trigger('keydown', { key: 'ArrowDown' })
    expect(text()).toBe('Alphacheck')
    wrapper.unmount()
  })

  it('takes its button content from the slot: the content names the button, the label names the menu', async () => {
    phone(false)
    const wrapper = mount(MenuButton, {
      props: { label: 'Your account', items, buttonClass: 'card' },
      slots: { default: () => h('span', 'Anna Andersson') },
      attachTo: document.body,
    })
    const button = wrapper.get('[data-test=menu-button]')
    expect(button.attributes('aria-label')).toBeUndefined()
    expect(button.text()).toBe('Anna Andersson')
    expect(button.classes()).toEqual(['card'])
    await button.trigger('click')
    await flushPromises()
    expect(openMenu().attributes('aria-label')).toBe('Your account')
    wrapper.unmount()
  })

  it('is a bottom sheet on a phone: a modal dialog titled by the name, every item (a disabled one shown as such) and Close', async () => {
    phone(true)
    dialogs()
    const withCompare = [{ key: 'phone-only', label: 'Compare with published', phoneOnly: true }, ...items]
    const wrapper = mount(MenuButton, { props: { label: 'More actions for Demo', items: withCompare }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('[role=menu]')).toBeNull()
    const sheet = document.body.querySelector('dialog')!
    expect(sheet.textContent).toContain('More actions for Demo')
    const labels = [...sheet.querySelectorAll('[data-test=menu-sheet] button')].map((b) => b.textContent?.trim())
    expect(labels).toEqual(['Compare with published', 'Edit name and contact', 'Change department', 'Copy', 'Delete…'])
    expect(sheet.querySelector('[data-test=menu-close]')?.textContent?.trim()).toBe('Close')
    expect(sheet.querySelector('[data-test=menu-move]')?.getAttribute('aria-disabled')).toBe('true')
    ;(sheet.querySelector('[data-test=menu-move]') as HTMLButtonElement).click()
    expect(wrapper.emitted('select')).toBeUndefined()
    ;(sheet.querySelector('[data-test=menu-copy]') as HTMLButtonElement).click()
    await flushPromises()
    expect(wrapper.emitted('select')).toEqual([['copy']])
    expect(document.body.querySelector('dialog')).toBeNull()
    wrapper.unmount()
  })

  it('shows a group in the sheet as pressed buttons under its name, and Close returns focus to the button', async () => {
    phone(true)
    dialogs()
    const grouped: MenuItem[] = [
      { key: 'a', label: 'Alpha', group: 'Pick', checked: true },
      { key: 'b', label: 'Beta', group: 'Pick', checked: false },
    ]
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items: grouped }, attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]')
    ;(button.element as HTMLElement).focus()
    await button.trigger('click')
    await flushPromises()
    const sheet = document.body.querySelector('dialog')!
    const group = sheet.querySelector('[role=group]')!
    expect(document.getElementById(group.getAttribute('aria-labelledby')!)?.textContent).toBe('Pick')
    expect([...group.querySelectorAll('button')].map((b) => b.getAttribute('aria-pressed'))).toEqual(['true', 'false'])
    ;(sheet.querySelector('[data-test=menu-close]') as HTMLButtonElement).click()
    await flushPromises()
    expect(document.body.querySelector('dialog')).toBeNull()
    expect(document.activeElement).toBe(button.element)
    wrapper.unmount()
  })

  it('leaves a phone-only item out on a wide screen, and shows no button when nothing else is left', async () => {
    phone(false)
    const only = mount(MenuButton, { props: { label: 'More actions', items: [{ key: 'phone-only', label: 'Compare with published', phoneOnly: true }] }, attachTo: document.body })
    expect(only.find('[data-test=menu-button]').exists()).toBe(false)
    only.unmount()
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items: [{ key: 'phone-only', label: 'Compare', phoneOnly: true }, ...items] }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    expect(openMenu().find('[data-test=menu-phone-only]').exists()).toBe(false)
    wrapper.unmount()
  })
})
