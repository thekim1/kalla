import { DOMWrapper, type VueWrapper } from '@vue/test-utils'

/** The first button whose visible text contains `text` (icon buttons also carry the icon’s name as text). */
export function button(wrapper: VueWrapper, text: string) {
  const found = wrapper.findAll('button').find((b) => b.text().includes(text))
  if (!found) throw new Error(`No button containing "${text}". Buttons: ${wrapper.findAll('button').map((b) => b.text()).join(' | ')}`)
  return found
}

export function buttonByLabel(wrapper: VueWrapper, label: string) {
  const found = wrapper.find(`button[aria-label="${label}"]`)
  if (!found.exists()) throw new Error(`No button labelled "${label}"`)
  return found
}

/**
 * The open ⋯ menu (MenuButton teleports it to the body, out of the wrapper's reach): the last one opened, since a
 * spec that leaves its wrapper mounted can leave an earlier menu open.
 */
export function openMenu(): DOMWrapper<Element> {
  const el = [...document.body.querySelectorAll('[data-test=menu]')].at(-1)
  if (!el) throw new Error('No ⋯ menu is open')
  return new DOMWrapper(el)
}
