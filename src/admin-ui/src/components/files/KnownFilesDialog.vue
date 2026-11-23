<script setup lang="ts">
import { computed } from 'vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDialog from '@/components/ui/UiDialog.vue'
import { COMMON, FILES } from '@/utils/labels'
import type { KnownChoice } from './files'

// Added files with the name and folder of files already in the source (admin design §5.5.7 point 6; support case SA-11):
// one question for the whole upload. Replace is the default (focused, so Enter takes it); closing without a choice
// cancels the upload, which leaves the files in the list.
const props = defineProps<{ paths: string[] }>()
const emit = defineEmits<{ (e: 'choose', choice: KnownChoice | null): void }>()

/** The most paths listed; a re-uploaded folder of hundreds says how many more. */
const MAX_ROWS = 50

const shown = computed(() => props.paths.slice(0, MAX_ROWS))
</script>

<template>
  <UiDialog
    :open="paths.length > 0" :title="FILES.knownTitle(paths.length)" :description="FILES.knownLead(paths.length)"
    data-test="known-files" @update:open="(open: boolean) => { if (!open) emit('choose', null) }"
  >
    <ul :aria-label="FILES.knownList" class="grid max-h-60 gap-1 overflow-y-auto text-small" data-test="known-list">
      <li v-for="path in shown" :key="path" class="break-all font-medium">{{ path }}</li>
      <li v-if="paths.length > shown.length" class="text-fg-3">{{ FILES.moreFiles(paths.length - shown.length) }}</li>
    </ul>
    <template #actions>
      <UiButton data-test="known-cancel" @click="emit('choose', null)">{{ COMMON.cancel }}</UiButton>
      <UiButton data-test="known-keep" @click="emit('choose', 'keep')">{{ FILES.keepBoth }}</UiButton>
      <UiButton variant="primary" data-autofocus data-test="known-replace" @click="emit('choose', 'replace')">{{ FILES.replaceKnown(paths.length) }}</UiButton>
    </template>
  </UiDialog>
</template>
