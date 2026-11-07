<script setup lang="ts">
import CheckField from '@/components/ui/CheckField.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextField from '@/components/ui/TextField.vue'
import { SOURCE } from '@/utils/labels'
import type { FetchMode } from './fetchMode'

// JavaScript pages (admin design §5.5.3; connectors design §8.1, §8.3) in the URL-list and crawl forms: Fetch pages with
// (fetchMode) and, for a browser, Content is ready when this element has text (readySelector) and Scroll to load more
// content (scrollToLoad). The form sends the browser-only settings only with a browser (withFetchSettings).

defineProps<{ idPrefix: string; fetchMode: FetchMode; readySelector: string; scrollToLoad: boolean; readySelectorError?: string | null }>()
const emit = defineEmits<{
  (e: 'update:fetchMode', value: FetchMode): void
  (e: 'update:readySelector', value: string): void
  (e: 'update:scrollToLoad', value: boolean): void
}>()

const options = (['http', 'browser'] as const).map((m) => ({ value: m, label: SOURCE.fetchModes[m] }))
</script>

<template>
  <div class="grid gap-5" data-test="fetch-mode-fields">
    <SelectField
      :id="`${idPrefix}-fetch-mode`" :model-value="fetchMode" :label="SOURCE.fetchMode" :hint="SOURCE.fetchModeHint" :options="options"
      @update:model-value="emit('update:fetchMode', $event as FetchMode)"
    />
    <template v-if="fetchMode === 'browser'">
      <TextField
        :id="`${idPrefix}-ready-selector`" :model-value="readySelector" :label="SOURCE.readySelector" :hint="SOURCE.readySelectorHint"
        :error="readySelectorError" mono @update:model-value="emit('update:readySelector', $event)"
      />
      <CheckField
        :model-value="scrollToLoad" :label="SOURCE.scrollToLoad" :description="SOURCE.scrollToLoadHint" data-test="scroll-to-load"
        @update:model-value="emit('update:scrollToLoad', $event)"
      />
    </template>
  </div>
</template>
