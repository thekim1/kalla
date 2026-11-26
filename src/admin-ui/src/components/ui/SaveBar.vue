<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { UNSAVED as L } from '@/utils/labels'
import ConfirmDialog from './ConfirmDialog.vue'
import UiButton from './UiButton.vue'

/**
 * The save bar (layout rules in docs/admin-ui.md, rule 7): "n unsaved changes · Discard · Save changes", with the
 * consequence when there is one. A labelled region that appears with a polite announcement; sticky at the bottom of the
 * page, and on a phone fixed above the bottom menu with 44 px buttons sharing the width. While it shows, the page's
 * scroll-padding-bottom is what it covers (its height, and on a phone the bottom menu under it too), so a focused field
 * is never hidden behind it; the field being edited when it appears is scrolled clear of it as well. On a phone a spacer
 * of its height under the form keeps the end of the page clear of it. Discard asks once. When the bar goes (after a save
 * or a discard) with focus in it, focus returns to the field last edited, not to the body.
 *
 * The `phone` slot holds an action the page offers from the bar on a phone (Källa's assistant: *Try the draft*):
 * shown beside the buttons while there are changes, and on its own in the same place while there are none.
 */
const props = defineProps<{ changes: number; saving?: boolean; consequence?: string; disabled?: boolean }>()
const slots = defineSlots<{ phone?: () => unknown }>()
const emit = defineEmits<{ (e: 'save'): void; (e: 'discard'): void }>()

const bar = ref<HTMLElement | null>(null)
/** The phone's strip with the page's action while nothing is unsaved (hidden from 768 px, then it covers nothing). */
const strip = ref<HTMLElement | null>(null)
const announcement = ref('')
const discarding = ref(false)
const height = ref(0)

function pad(px: number): void {
  height.value = px
  document.documentElement.style.scrollPaddingBottom = px ? `${px}px` : ''
}

// The field last focused outside the bar: where focus goes back to when the bar goes with focus in it.
let lastField: HTMLElement | null = null
const onFocusIn = (e: FocusEvent) => {
  const target = e.target as HTMLElement | null
  if (target && !bar.value?.contains(target) && target !== document.body) lastField = target
}
document.addEventListener('focusin', onFocusIn)

/**
 * How much of the window's bottom the bar takes. Fixed (on a phone), from its top to the window's bottom, so the bottom
 * menu under it counts too. Sticky (wider), its own height: it sits inside its form, so when the form ends mid-window it
 * is in the page, not at the window's bottom.
 */
function coveredHeight(el: HTMLElement | null = bar.value): number {
  if (!el) return 0
  if (getComputedStyle(el).position !== 'fixed') return el.offsetHeight
  const rect = el.getBoundingClientRect()
  const fromBottom = rect.height > 0 ? Math.round(window.innerHeight - rect.top) : 0
  return Math.max(el.offsetHeight, fromBottom)
}

/** The bar appears while a field has focus (the one just edited): when it now covers the field, scroll it clear. */
function revealFocused(): void {
  const active = document.activeElement as HTMLElement | null
  if (!active || active === document.body || !bar.value || bar.value.contains(active)) return
  if (active.getBoundingClientRect().bottom > bar.value.getBoundingClientRect().top) active.scrollIntoView?.({ block: 'nearest' })
}

watch(
  () => props.changes > 0,
  async (shown) => {
    announcement.value = shown ? L.count(props.changes) : ''
    const focusInBar = !shown && !!bar.value?.contains(document.activeElement)
    await nextTick()
    // While nothing is unsaved, the phone's strip covers the bottom instead (nothing where it is hidden).
    pad(shown ? coveredHeight() : strip.value && strip.value.getBoundingClientRect().height > 0 ? coveredHeight(strip.value) : 0)
    if (shown) revealFocused()
    if (focusInBar || (!shown && document.activeElement === document.body)) {
      if (lastField?.isConnected) lastField.focus()
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => {
  document.removeEventListener('focusin', onFocusIn)
  pad(0)
})
</script>

<template>
  <!-- Always in the page, so the announcement is heard when the bar appears. -->
  <p class="sr-only" aria-live="polite" data-test="save-bar-announcement">{{ announcement }}</p>
  <section
    v-if="changes > 0" ref="bar" :aria-label="L.region"
    class="material sticky bottom-4 z-topbar mt-6 flex flex-wrap items-center gap-3 rounded-card p-4 shadow-2 max-md:fixed max-md:inset-x-0 max-md:bottom-[calc(4rem+env(safe-area-inset-bottom))] max-md:mt-0 max-md:rounded-none"
    data-test="save-bar"
  >
    <p class="min-w-0 flex-1 text-small">
      <span class="font-medium" data-test="save-bar-count">{{ L.count(changes) }}</span>
      <span v-if="consequence" class="text-fg-2" data-test="save-bar-consequence"> · {{ consequence }}</span>
    </p>
    <div class="flex gap-2 max-md:w-full max-md:[&>*]:h-11 max-md:[&>*]:flex-1">
      <div v-if="slots.phone" class="contents md:hidden"><slot name="phone" /></div>
      <UiButton :disabled="saving" data-test="save-bar-discard" @click="discarding = true">{{ L.discard }}</UiButton>
      <UiButton variant="primary" :loading="saving" :disabled="disabled" data-test="save-bar-save" @click="emit('save')">{{ L.save }}</UiButton>
    </div>
    <ConfirmDialog
      v-model:open="discarding" :title="L.discardTitle(changes)" :consequence="L.discardConsequence" :confirm-label="L.discard" danger
      @confirm="emit('discard')"
    />
  </section>
  <div
    v-if="changes === 0 && slots.phone"
    ref="strip"
    class="fixed inset-x-0 bottom-[calc(4rem+env(safe-area-inset-bottom))] z-topbar flex p-3 md:hidden [&>*]:h-11 [&>*]:flex-1"
    data-test="save-bar-phone"
  >
    <slot name="phone" />
  </div>
  <!-- On a phone the bar is fixed above the bottom menu: this keeps the end of the form clear of it. -->
  <div
    v-if="changes > 0 || slots.phone" class="md:hidden" :style="{ height: changes > 0 ? `${height}px` : '68px' }" aria-hidden="true"
    data-test="save-bar-spacer"
  />
</template>
