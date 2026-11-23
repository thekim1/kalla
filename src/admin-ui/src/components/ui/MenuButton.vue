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
}

/**
 * The ⋯ menu (admin design §5.12 rule 1 and its accessibility table): an APG menu button. A real button with
 * aria-haspopup="menu", aria-expanded and a name that says whose actions they are; arrow keys, Home and End move,
 * Enter and Space act, Esc and Tab close, and Esc returns focus to the button. Below 760 px the menu is a bottom sheet:
 * a modal dialog with a focus trap and a Close button (UiDialog). Built in Källa until Fors takes it (§5.12, Shared
 * components).
 */
const props = withDefaults(defineProps<{ label: string; items: MenuItem[]; size?: 'sm' | 'md' }>(), { size: 'md' })
const emit = defineEmits<{ (e: 'select', key: string): void }>()

const open = ref(false)
const button = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const menuId = `menu-${useId()}`
const enabled = computed(() => props.items.filter((i) => !i.disabled))

// The phone's sheet (rule 10): the same breakpoint as Tailwind's md.
const phoneQuery = typeof window !== 'undefined' && window.matchMedia ? window.matchMedia('(max-width: 759px)') : null
const phone = ref(phoneQuery?.matches ?? false)
const onQuery = (e: MediaQueryListEvent) => (phone.value = e.matches)

function menuItems(): HTMLElement[] {
  return [...(menu.value?.querySelectorAll<HTMLElement>('[role=menuitem]:not([aria-disabled=true])') ?? [])]
}

async function show(focus: 'first' | 'last' = 'first'): Promise<void> {
  open.value = true
  if (phone.value) return
  await nextTick()
  const items = menuItems()
  ;(focus === 'first' ? items[0] : items.at(-1))?.focus()
}

function close(returnFocus: boolean): void {
  if (!open.value) return
  open.value = false
  if (returnFocus) void nextTick(() => button.value?.focus())
}

function choose(item: MenuItem): void {
  if (item.disabled) return
  close(!phone.value)
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
  } else if (e.key === 'Tab') close(false)
}

function onDocumentClick(e: MouseEvent): void {
  if (!open.value || phone.value) return
  const target = e.target as Node
  if (!menu.value?.contains(target) && !button.value?.contains(target)) close(false)
}

onMounted(() => {
  document.addEventListener('click', onDocumentClick, true)
  phoneQuery?.addEventListener?.('change', onQuery)
})
onBeforeUnmount(() => {
  document.removeEventListener('click', onDocumentClick, true)
  phoneQuery?.removeEventListener?.('change', onQuery)
})
</script>

<template>
  <div class="relative inline-flex">
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
    <ul
      v-if="open && !phone"
      :id="menuId"
      ref="menu"
      role="menu"
      tabindex="-1"
      :aria-label="label"
      class="material-overlay absolute right-0 top-full z-drawer mt-1 min-w-56 rounded-card p-1 shadow-2"
      data-test="menu"
      @keydown="onMenuKey"
    >
      <li v-for="item in items" :key="item.key" role="none">
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
    <UiDialog v-if="phone" :open="open" :title="label" @update:open="close(true)">
      <ul class="grid gap-1" data-test="menu-sheet">
        <li v-for="item in enabled" :key="item.key">
          <button
            type="button"
            class="flex min-h-11 w-full items-center gap-2 rounded-control px-3 text-left hover:bg-hover"
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
