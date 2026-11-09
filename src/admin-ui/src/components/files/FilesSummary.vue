<script setup lang="ts">
import { computed } from 'vue'
import type { UploadSummary } from '@/api/types'
import { FILES } from '@/utils/labels'

// The summary bar of the Files card (admin design §5.5.7): "120 files · 34 processing · 84 indexed · 2 failed" and a
// progress bar of the processed share, (indexed + failed) / total.
const props = defineProps<{ summary: UploadSummary }>()

const done = computed(() => (props.summary.total === 0 ? 0 : Math.round(((props.summary.indexed + props.summary.failed) / props.summary.total) * 100)))
</script>

<template>
  <div class="grid gap-2" data-test="files-summary">
    <p class="tabular text-small" aria-live="polite" data-test="files-summary-text">
      {{ FILES.summary(summary.total, summary.processing, summary.indexed, summary.failed) }}
    </p>
    <div
      v-if="summary.total > 0"
      role="progressbar"
      :aria-label="FILES.progress"
      :aria-valuenow="done"
      aria-valuemin="0"
      aria-valuemax="100"
      :aria-valuetext="`${done}%`"
      class="h-2 overflow-hidden rounded-full bg-sunken"
      data-test="files-progress"
    >
      <div class="h-full rounded-full bg-chart transition-[width] duration-base ease-emphasized" :style="{ width: `${done}%` }" />
    </div>
  </div>
</template>
