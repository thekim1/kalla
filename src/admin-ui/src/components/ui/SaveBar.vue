<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { UNSAVED as L } from '@/utils/labels'
import ConfirmDialog from './ConfirmDialog.vue'
import UiButton from './UiButton.vue'

/**
 * The save bar (admin design §5.12 rule 7 and *Saving*): "n unsaved changes · Discard · Save changes", with the
 * consequence when there is one. A labelled region that appears with a polite announcement; sticky at the bottom of the
 * page, and on a phone fixed above the bottom menu with 44 px buttons sharing the width. While it shows, the page's
 * scroll-padding-bottom is its height, so a focused field is never hidden behind it, and on a phone a spacer of its
 * height under the form keeps the end of the page clear of it. Discard asks once. When the bar goes (after a save or a
 * discard) with focus in it, focus returns to the field last edited, not to the body. Built in Källa until Fors takes
 * it (§5.12, Shared components).
 */
const props = defineProps<{ changes: number; saving?: boolean; consequence?: string; disabled?: boolean }>()
const emit = defineEmits<{ (e: 'save'): void; (e: 'discard'): void }>()

const bar = ref<HTMLElement | null>(null)
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

watch(
  () => props.changes > 0,
  async (shown) => {
    announcement.value = shown ? L.count(props.changes) : ''
    const focusInBar = !shown && !!bar.value?.contains(document.activeElement)
    await nextTick()
    pad(shown ? (bar.value?.offsetHeight ?? 0) : 0)
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
      <UiButton :disabled="saving" data-test="save-bar-discard" @click="discarding = true">{{ L.discard }}</UiButton>
      <UiButton variant="primary" :loading="saving" :disabled="disabled" data-test="save-bar-save" @click="emit('save')">{{ L.save }}</UiButton>
    </div>
    <ConfirmDialog
      v-model:open="discarding" :title="L.discardTitle(changes)" :consequence="L.discardConsequence" :confirm-label="L.discard" danger
      @confirm="emit('discard')"
    />
  </section>
  <!-- On a phone the bar is fixed above the bottom menu: this keeps the end of the form clear of it. -->
  <div v-if="changes > 0" class="md:hidden" :style="{ height: `${height}px` }" aria-hidden="true" data-test="save-bar-spacer" />
</template>
