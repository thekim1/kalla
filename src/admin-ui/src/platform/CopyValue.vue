<script setup lang="ts">
import UiButton from '@/components/ui/UiButton.vue'
import { useCopy } from '@/composables/useCopy'
import { PLATFORM } from '@/utils/labels'

// A value with a copy button (admin design §5.8, §5.9): a row's id, a secret or the stack lines. `copyLabel` names
// the button when there are several on a page; "Copied" is announced in a status region.
withDefaults(defineProps<{ value: string; copyLabel?: string; block?: boolean }>(), { copyLabel: PLATFORM.form.copy })
const { copied, copy } = useCopy()
</script>

<template>
  <div class="flex flex-wrap items-center gap-2" :class="block ? 'flex-col items-stretch' : ''">
    <code class="break-all font-mono" :class="block ? 'whitespace-pre-wrap rounded-card bg-sunken p-3' : 'text-small'" data-test="copy-value">{{ value }}</code>
    <div class="flex items-center gap-2">
      <UiButton size="sm" icon="content_copy" data-test="copy" @click="copy(value)">{{ copyLabel }}</UiButton>
      <span role="status" class="text-small text-fg-3">{{ copied ? PLATFORM.form.copied : '' }}</span>
    </div>
  </div>
</template>
