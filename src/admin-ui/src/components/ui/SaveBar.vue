<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { UNSAVED as L } from '@/utils/labels'
import ConfirmDialog from './ConfirmDialog.vue'
import UiButton from './UiButton.vue'

/**
 * The save bar (admin design §5.12 rule 7 and *Saving*): "n unsaved changes · Discard · Save changes", with the
 * consequence when there is one. A labelled region that appears with a polite announcement; sticky at the bottom of the
 * page, and on a phone fixed above the bottom menu with 44 px buttons sharing the width. While it shows, the page's
 * scroll-padding-bottom is its height, so a focused field is never hidden behind it. Discard asks once. Built in Källa
 * until Fors takes it (§5.12, Shared components).
 */
const props = defineProps<{ changes: number; saving?: boolean; consequence?: string; disabled?: boolean }>()
const emit = defineEmits<{ (e: 'save'): void; (e: 'discard'): void }>()

const bar = ref<HTMLElement | null>(null)
const announcement = ref('')
const discarding = ref(false)

function pad(px: number): void {
  document.documentElement.style.scrollPaddingBottom = px ? `${px}px` : ''
}

watch(
  () => props.changes > 0,
  async (shown) => {
    announcement.value = shown ? L.count(props.changes) : ''
    await nextTick()
    pad(shown ? (bar.value?.offsetHeight ?? 0) : 0)
  },
  { immediate: true },
)
onBeforeUnmount(() => pad(0))
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
</template>
