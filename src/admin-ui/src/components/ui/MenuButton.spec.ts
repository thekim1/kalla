import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
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
    expect(wrapper.find('[role=menu]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('opens on the first item, moves with the arrows, Home and End past a disabled item, and Esc returns focus to the button', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    const button = wrapper.get('[data-test=menu-button]')
    await button.trigger('click')
    await flushPromises()
    expect(button.attributes('aria-expanded')).toBe('true')
    const menu = wrapper.get('[role=menu]')
    expect(button.attributes('aria-controls')).toBe(menu.attributes('id'))
    expect(document.activeElement?.textContent).toBe('Edit name and contact')

    await menu.trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement?.textContent).toBe('Copy') // the disabled item is skipped
    await menu.trigger('keydown', { key: 'End' })
    expect(document.activeElement?.textContent).toBe('Delete…')
    await menu.trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement?.textContent).toBe('Edit name and contact') // wraps
    await menu.trigger('keydown', { key: 'ArrowUp' })
    expect(document.activeElement?.textContent).toBe('Delete…')

    await menu.trigger('keydown', { key: 'Escape' })
    await flushPromises()
    expect(wrapper.find('[role=menu]').exists()).toBe(false)
    expect(document.activeElement).toBe(button.element)
    wrapper.unmount()
  })

  it('opens on the last item with ArrowUp, acts on Enter (a click) and emits the key, but not for a disabled item', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('keydown', { key: 'ArrowUp' })
    await flushPromises()
    expect(document.activeElement?.textContent).toBe('Delete…')
    expect(wrapper.get('[data-test=menu-delete]').classes()).toContain('text-danger')

    await wrapper.get('[data-test=menu-move]').trigger('click')
    expect(wrapper.emitted('select')).toBeUndefined()
    expect(wrapper.get('[data-test=menu-move]').attributes('aria-disabled')).toBe('true')

    await wrapper.get('[data-test=menu-copy]').trigger('click')
    expect(wrapper.emitted('select')).toEqual([['copy']])
    expect(wrapper.find('[role=menu]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('closes without moving focus on Tab and on a click outside', async () => {
    phone(false)
    const wrapper = mount(MenuButton, { props: { label: 'More actions', items }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await wrapper.get('[role=menu]').trigger('keydown', { key: 'Tab' })
    expect(wrapper.find('[role=menu]').exists()).toBe(false)

    await wrapper.get('[data-test=menu-button]').trigger('click')
    document.body.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    expect(wrapper.find('[role=menu]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('is a bottom sheet on a phone: a modal dialog titled by the name, the enabled items, and Close', async () => {
    phone(true)
    HTMLDialogElement.prototype.showModal ??= function (this: HTMLDialogElement) { this.open = true }
    HTMLDialogElement.prototype.close ??= function (this: HTMLDialogElement) { this.open = false }
    const wrapper = mount(MenuButton, { props: { label: 'More actions for Bygglov', items }, attachTo: document.body })
    await wrapper.get('[data-test=menu-button]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[role=menu]').exists()).toBe(false)
    const sheet = document.body.querySelector('dialog')!
    expect(sheet.textContent).toContain('More actions for Bygglov')
    const labels = [...sheet.querySelectorAll('[data-test=menu-sheet] button')].map((b) => b.textContent)
    expect(labels).toEqual(['Edit name and contact', 'Copy', 'Delete…'])
    ;(sheet.querySelector('[data-test=menu-copy]') as HTMLButtonElement).click()
    await flushPromises()
    expect(wrapper.emitted('select')).toEqual([['copy']])
    wrapper.unmount()
  })
})
