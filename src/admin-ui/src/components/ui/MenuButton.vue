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
  /** Red, and last by convention (admin design §5.12 rule 1); the caller keeps its confirmation dialog. */
  danger?: boolean
  disabled?: boolean
  /** Only in the phone's sheet: an action that is a header button on a wider screen (§5.12, *On a phone*). */
  phoneOnly?: boolean
}

/**
 * The ⋯ menu (admin design §5.12 rule 1 and its accessibility table): an APG menu button. A real button with
 * aria-haspopup="menu", aria-expanded and a name that says whose actions they are; arrow keys, Home and End move,
 * Enter and Space act, Esc and Tab close, and Esc returns focus to the button. Below 760 px the menu is a bottom sheet:
 * a modal dialog with a focus trap and a Close button (UiDialog). Built in Källa until Fors takes it (§5.12, Shared
 * components).
 *
 * The open menu is teleported to the body with fixed coordinates, so a table's or card's overflow (and a glass theme's
 * backdrop filter, which makes a containing block) cannot clip it; it opens upwards when there is no room below and
 * follows the button when the page scrolls or the window resizes.
 * Choosing an item focuses the ⋯ button before `select` is emitted, so a dialog the parent opens takes the button as
 * its opener and returns focus there when it closes. Disabled items stay focusable, as APG keeps them.
 */
const props = withDefaults(defineProps<{ label: string; items: MenuItem[]; size?: 'sm' | 'md' }>(), { size: 'md' })
const emit = defineEmits<{ (e: 'select', key: string): void }>()

const open = ref(false)
const button = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const menuId = `menu-${useId()}`
const position = ref<Record<string, string>>({})

// The phone's sheet (rule 10): the same breakpoint as Tailwind's md.
const phoneQuery = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(max-width: 759px)') : null
const phone = ref(phoneQuery?.matches ?? false)
const onQuery = (e: MediaQueryListEvent) => (phone.value = e.matches)
const desktopItems = computed(() => props.items.filter((i) => !i.phoneOnly))

function menuItems(): HTMLElement[] {
  return [...(menu.value?.querySelectorAll<HTMLElement>('[role=menuitem]') ?? [])]
}

/** Below the button, right-aligned to it; above it when the menu would run past the bottom of the window. */
function place(): void {
  const b = button.value?.getBoundingClientRect()
  if (!b) return
  const height = menu.value?.offsetHeight ?? 0
  const gap = 4
  const below = b.bottom + gap + height <= window.innerHeight || b.top - gap - height < 0
  position.value = {
    right: `${Math.max(0, document.documentElement.clientWidth - b.right)}px`,
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
  } else if (e.key === 'Tab') close(true) // from the button, Tab's own move goes on to the next field
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
</script>

<template>
  <!-- Nothing to show at this width (only phone-only items on a wide screen): no button. -->
  <div v-if="phone ? items.length : desktopItems.length" class="relative inline-flex">
    <button
      ref="button"
      type="button"
      class="btn bg-surface text-fg shadow-inset-strong hover:bg-surface-2 max-md:h-11 max-md:min-w-11"
      :class="size === 'sm' ? 'h-ctl-sm px-2' : 'h-ctl px-2.5'"
      aria-haspopup="menu"
      :aria-expanded="open ? 'true' : 'false'"
      :aria-controls="open && !phone ? menuId : undefined"
      :aria-label="label"
      data-test="menu-button"
      @click="open ? close(false) : show()"
      @keydown="onButtonKey"
    >
      <AppIcon name="more_horiz" :size="20" />
    </button>
    <Teleport to="body">
      <ul
        v-if="open && !phone"
        :id="menuId"
        ref="menu"
        role="menu"
        tabindex="-1"
        :aria-label="label"
        class="material-overlay fixed z-drawer min-w-56 rounded-card p-1 shadow-2"
        :style="position"
        data-test="menu"
        @keydown="onMenuKey"
      >
        <li v-for="item in desktopItems" :key="item.key" role="none">
          <button
            type="button"
            role="menuitem"
            tabindex="-1"
            :aria-disabled="item.disabled ? 'true' : undefined"
            class="flex w-full items-center gap-2 rounded-control px-3 py-2 text-left hover:bg-hover focus-visible:bg-hover aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
            :class="item.danger ? 'text-danger' : 'text-fg'"
            :data-test="`menu-${item.key}`"
            @click="choose(item)"
          >
            <AppIcon v-if="item.icon" :name="item.icon" :size="18" />{{ item.label }}
          </button>
        </li>
      </ul>
    </Teleport>
    <UiDialog v-if="phone" :open="open" :title="label" @update:open="close(true)">
      <ul class="grid gap-1" data-test="menu-sheet">
        <li v-for="item in items" :key="item.key">
          <button
            type="button"
            :aria-disabled="item.disabled ? 'true' : undefined"
            class="flex min-h-11 w-full items-center gap-2 rounded-control px-3 text-left hover:bg-hover aria-disabled:cursor-not-allowed aria-disabled:opacity-50"
            :class="item.danger ? 'text-danger' : 'text-fg'"
            :data-test="`menu-${item.key}`"
            @click="choose(item)"
          >
            <AppIcon v-if="item.icon" :name="item.icon" :size="20" />{{ item.label }}
          </button>
        </li>
      </ul>
      <template #actions>
        <UiButton data-test="menu-close" @click="close(true)">{{ COMMON.close }}</UiButton>
      </template>
    </UiDialog>
  </div>
</template>
