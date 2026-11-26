<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId } from 'vue'
import { COMMON } from '@/utils/labels'
import AppIcon from './AppIcon.vue'
import UiButton from './UiButton.vue'
import UiDialog from './UiDialog.vue'

export interface MenuItem {
  key: string
  label: string
  icon?: string
  /** Red, and last by convention (layout rule 1); the caller keeps its own confirmation dialog. */
  danger?: boolean
  disabled?: boolean
  /** Only in the phone's sheet: an action that is a button of its own on a wider screen (layout rule 10). */
  phoneOnly?: boolean
  /** A choice of one (with the items of the same `group`): a menuitemradio with aria-checked, a pressed button in the sheet. */
  checked?: boolean
  /** Items next to each other with the same group are shown together under its name, apart from the rest. */
  group?: string
}

interface Section {
  group?: string
  items: MenuItem[]
}

/**
 * The ⋯ menu (layout rules in docs/admin-ui.md, rule 1), and the user menu: an APG menu button. A real button with
 * aria-haspopup="menu", aria-expanded and a name that says what it is; arrow
 * keys, Home and End move between the items, Enter and Space act, Esc and Tab close, and Esc returns focus to the
 * button. Below Tailwind's md (768 px) the menu is a bottom sheet: a modal dialog with a focus trap and a Close button
 * (UiDialog). Ported from Källa's MenuButton; groups, choices, `align` and the slot are Fors's (thekim1/fors#127).
 *
 * The open menu is teleported to the body with fixed coordinates, so an overflow (and a glass theme's backdrop filter,
 * which makes a containing block) cannot clip it; it opens upwards when there is no room below and follows the button
 * when the page scrolls or the window resizes. Choosing an item focuses the button before `select` is emitted, so a
 * dialog the parent opens takes the button as its opener and returns focus there when it closes. Disabled items stay
 * focusable, as APG keeps them.
 *
 * The default slot replaces the ⋯ icon; the slot's content is then the button's name, and `label` names the menu.
 */
const props = withDefaults(
  defineProps<{
    label: string
    items: MenuItem[]
    size?: 'sm' | 'md'
    /** Which edge of the button the menu lines up with: its right edge (end) or its left edge (start). */
    align?: 'start' | 'end'
    /** Classes for the button in place of the ⋯ button's own, with the default slot. */
    buttonClass?: string
  }>(),
  { size: 'md', align: 'end', buttonClass: undefined },
)
const emit = defineEmits<{ (e: 'select', key: string): void }>()
const slots = defineSlots<{ default?: () => unknown }>()

const open = ref(false)
const button = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const menuId = `menu-${useId()}`
const position = ref<Record<string, string>>({})

// The phone's sheet: the same breakpoint as Tailwind's md, below which the shell has no side menu.
const phoneQuery = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(max-width: 767px)') : null
const phone = ref(phoneQuery?.matches ?? false)
const onQuery = (e: MediaQueryListEvent) => (phone.value = e.matches)
const desktopItems = computed(() => props.items.filter((i) => !i.phoneOnly))

function sections(items: MenuItem[]): Section[] {
  const result: Section[] = []
  for (const item of items) {
    const last = result.at(-1)
    if (last && last.group === item.group) last.items.push(item)
    else result.push({ group: item.group, items: [item] })
  }
  return result
}
const desktopSections = computed(() => sections(desktopItems.value))
const phoneSections = computed(() => sections(props.items))
const sectionId = (index: number) => `${menuId}-group-${index}`

function menuItems(): HTMLElement[] {
  return [...(menu.value?.querySelectorAll<HTMLElement>('[role=menuitem], [role=menuitemradio]') ?? [])]
}

/** Below the button, lined up with its `align` edge; above it when the menu would run past the bottom of the window. */
function place(): void {
  const b = button.value?.getBoundingClientRect()
  if (!b) return
  const height = menu.value?.offsetHeight ?? 0
  const gap = 4
  const below = b.bottom + gap + height <= window.innerHeight || b.top - gap - height < 0
  position.value = {
    ...(props.align === 'start'
      ? { left: `${Math.max(0, b.left)}px` }
      : { right: `${Math.max(0, document.documentElement.clientWidth - b.right)}px` }),
    ...(below ? { top: `${b.bottom + gap}px` } : { bottom: `${window.innerHeight - b.top + gap}px` }),
  }
}

async function show(focus: 'first' | 'last' = 'first'): Promise<void> {
  open.value = true
  if (phone.value) return
  place()
  await nextTick()
  place()
  const items = menuItems()
  ;(focus === 'first' ? items[0] : items.at(-1))?.focus()
}

/** Closes; on a wide screen focus goes back to the button at once (the phone's sheet restores it itself). */
function close(returnFocus: boolean): void {
  if (!open.value) return
  if (returnFocus && !phone.value) button.value?.focus()
  open.value = false
}

function choose(item: MenuItem): void {
  if (item.disabled) return
  close(true)
  emit('select', item.key)
}

function onButtonKey(e: KeyboardEvent): void {
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault()
    void show(e.key === 'ArrowDown' ? 'first' : 'last')
  }
}

function onMenuKey(e: KeyboardEvent): void {
  const items = menuItems()
  const index = items.indexOf(document.activeElement as HTMLElement)
  const move = (to: number) => {
    e.preventDefault()
    items[(to + items.length) % items.length]?.focus()
  }
  if (e.key === 'ArrowDown') move(index + 1)
  else if (e.key === 'ArrowUp') move(index - 1)
  else if (e.key === 'Home') move(0)
  else if (e.key === 'End') move(items.length - 1)
  else if (e.key === 'Escape') {
    e.preventDefault()
    close(true)
  } else if (e.key === 'Tab') close(true) // from the button, Tab's own move goes on to the next control
}

function onDocumentClick(e: MouseEvent): void {
  if (!open.value || phone.value) return
  const target = e.target as Node
  if (!menu.value?.contains(target) && !button.value?.contains(target)) close(false)
}

/** A scroll or resize moves the button: the open menu follows it (closing would drop a click already on its way). */
function onViewportChange(e: Event): void {
  if (!open.value || phone.value) return
  if (e.type === 'scroll' && e.target instanceof Node && menu.value?.contains(e.target)) return
  place()
}

onMounted(() => {
  document.addEventListener('click', onDocumentClick, true)
  window.addEventListener('resize', onViewportChange)
  window.addEventListener('scroll', onViewportChange, true)
  phoneQuery?.addEventListener?.('change', onQuery)
})
onBeforeUnmount(() => {
  document.removeEventListener('click', onDocumentClick, true)
  window.removeEventListener('resize', onViewportChange)
  window.removeEventListener('scroll', onViewportChange, true)
  phoneQuery?.removeEventListener?.('change', onQuery)
})

const itemClass = (item: MenuItem) => (item.danger ? 'text-danger' : 'text-fg')
</script>

<template>
  <!-- Nothing to show at this width (only phone-only items on a wide screen): no button. -->
  <div v-if="phone ? items.length : desktopItems.length" class="relative flex">
    <button
      ref="button"
      type="button"
      :class="
        slots.default
          ? buttonClass
          : ['btn bg-surface text-fg shadow-inset-strong hover:bg-surface-2 max-md:h-11 max-md:min-w-11', size === 'sm' ? 'h-ctl-sm px-2' : 'h-ctl px-2.5']
      "
      aria-haspopup="menu"
      :aria-expanded="open ? 'true' : 'false'"
      :aria-controls="open && !phone ? menuId : undefined"
      :aria-label="slots.default ? undefined : label"
      data-test="menu-button"
      @click="open ? close(false) : show()"
      @keydown="onButtonKey"
    >
      <slot><AppIcon name="more_horiz" :size="20" /></slot>
    </button>
    <Teleport to="body">
      <ul
        v-if="open && !phone"
        :id="menuId"
        ref="menu"
        role="menu"
        tabindex="-1"
        :aria-label="label"
        class="material-overlay fixed z-drawer min-w-64 rounded-card p-1 shadow-2"
        :style="position"
        data-test="menu"
        @keydown="onMenuKey"
      >
        <template v-for="(section, si) in desktopSections" :key="si">
          <li v-if="si > 0" role="separator" class="mx-2 my-1 h-px bg-border" />
          <li v-if="section.group" role="none">
            <span :id="sectionId(si)" class="block px-3 pb-1 pt-2 text-caption font-medium uppercase tracking-wide text-fg-3">{{ section.group }}</span>
            <ul role="group" :aria-labelledby="sectionId(si)">
              <li v-for="item in section.items" :key="item.key" role="none">
                <button
                  type="button"
                  :role="item.checked === undefined ? 'menuitem' : 'menuitemradio'"
                  :aria-checked="item.checked === undefined ? undefined : item.checked ? 'true' : 'false'"
                  tabindex="-1"
                  :aria-disabled="item.disabled ? 'true' : undefined"
                  class="flex w-full items-center gap-2 rounded-control px-3 py-2 text-left hover:bg-hover focus-visible:bg-hover aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                  :class="itemClass(item)"
                  :data-test="`menu-${item.key}`"
                  @click="choose(item)"
                >
                  <AppIcon v-if="item.icon" :name="item.icon" :size="18" /><span class="flex-1">{{ item.label }}</span>
                  <AppIcon v-if="item.checked" name="check" :size="18" />
                </button>
              </li>
            </ul>
          </li>
          <template v-else>
            <li v-for="item in section.items" :key="item.key" role="none">
              <button
                type="button"
                :role="item.checked === undefined ? 'menuitem' : 'menuitemradio'"
                :aria-checked="item.checked === undefined ? undefined : item.checked ? 'true' : 'false'"
                tabindex="-1"
                :aria-disabled="item.disabled ? 'true' : undefined"
                class="flex w-full items-center gap-2 rounded-control px-3 py-2 text-left hover:bg-hover focus-visible:bg-hover aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                :class="itemClass(item)"
                :data-test="`menu-${item.key}`"
                @click="choose(item)"
              >
                <AppIcon v-if="item.icon" :name="item.icon" :size="18" /><span class="flex-1">{{ item.label }}</span>
                <AppIcon v-if="item.checked" name="check" :size="18" />
              </button>
            </li>
          </template>
        </template>
      </ul>
    </Teleport>
    <UiDialog v-if="phone" :open="open" :title="label" @update:open="close(true)">
      <div class="grid gap-3" data-test="menu-sheet">
        <template v-for="(section, si) in phoneSections" :key="si">
          <div v-if="section.group" class="grid gap-1">
            <span :id="sectionId(si)" class="px-3 text-caption font-medium uppercase tracking-wide text-fg-3">{{ section.group }}</span>
            <ul role="group" :aria-labelledby="sectionId(si)" class="grid gap-1">
              <li v-for="item in section.items" :key="item.key">
                <button
                  type="button"
                  :aria-pressed="item.checked === undefined ? undefined : item.checked ? 'true' : 'false'"
                  :aria-disabled="item.disabled ? 'true' : undefined"
                  class="flex min-h-11 w-full items-center gap-2 rounded-control px-3 text-left hover:bg-hover aria-disabled:cursor-not-allowed aria-disabled:opacity-50 aria-pressed:bg-surface aria-pressed:font-semibold aria-pressed:shadow-1"
                  :class="itemClass(item)"
                  :data-test="`menu-${item.key}`"
                  @click="choose(item)"
                >
                  <AppIcon v-if="item.icon" :name="item.icon" :size="20" /><span class="flex-1">{{ item.label }}</span>
                  <AppIcon v-if="item.checked" name="check" :size="20" />
                </button>
              </li>
            </ul>
          </div>
          <ul v-else class="grid gap-1">
            <li v-for="item in section.items" :key="item.key">
              <button
                type="button"
                :aria-pressed="item.checked === undefined ? undefined : item.checked ? 'true' : 'false'"
                :aria-disabled="item.disabled ? 'true' : undefined"
                class="flex min-h-11 w-full items-center gap-2 rounded-control px-3 text-left hover:bg-hover aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
                :class="itemClass(item)"
                :data-test="`menu-${item.key}`"
                @click="choose(item)"
              >
                <AppIcon v-if="item.icon" :name="item.icon" :size="20" /><span class="flex-1">{{ item.label }}</span>
                <AppIcon v-if="item.checked" name="check" :size="20" />
              </button>
            </li>
          </ul>
        </template>
      </div>
      <template #actions>
        <UiButton data-test="menu-close" @click="close(true)">{{ COMMON.close }}</UiButton>
      </template>
    </UiDialog>
  </div>
</template>
