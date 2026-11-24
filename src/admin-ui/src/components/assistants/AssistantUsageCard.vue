<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { FeedbackSummary } from '@/api/types'
import { lastDays } from '@/components/costs/costs'
import AsyncState from '@/components/ui/AsyncState.vue'
import FilterSelect from '@/components/ui/FilterSelect.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useLoadable } from '@/stores/loadable'
import { formatDate, formatNumber, quietZero } from '@/utils/format'
import { COSTS, USAGE } from '@/utils/labels'

/**
 * The assistant's use (admin design §5.5.4 *Usage*): the date of last use, and the questions, not found and ratings of
 * a period, from the daily totals (`GET /api/feedback/summary?assistantId=`). The Cost card sits below it.
 */
const props = defineProps<{ assistantId: string }>()
const summary = useLoadable<FeedbackSummary | null>(null)
const days = ref('30')
const periodOptions = Object.entries(COSTS.periods).map(([value, label]) => ({ value, label }))

const load = () => summary.run(() => api.feedback.summary({ ...lastDays(Number(days.value)), assistantId: props.assistantId }))
watch(days, () => void load())
onMounted(load)
</script>

<template>
  <UiCard :title="USAGE.title" data-test="usage-card">
    <template #actions><FilterSelect v-model="days" :label="COSTS.period" :options="periodOptions" /></template>
    <AsyncState :loading="summary.loading.value" :error="summary.error.value" :empty="!summary.data.value" @retry="load()">
      <template v-if="summary.data.value">
        <p class="mb-4 text-fg-2" data-test="usage-last-used">
          {{ summary.data.value.lastUsedOn ? USAGE.lastUsed(formatDate(summary.data.value.lastUsedOn)) : USAGE.neverUsed }}
        </p>
        <dl class="grid grid-cols-3 gap-4 max-sm:grid-cols-1">
          <div>
            <dt class="text-small text-fg-2">{{ USAGE.questions }}</dt>
            <dd class="text-heading tabular" :class="quietZero(summary.data.value.totals.answers)" data-test="usage-questions">{{ formatNumber(summary.data.value.totals.answers) }}</dd>
          </div>
          <div>
            <dt class="text-small text-fg-2">{{ USAGE.notFound }}</dt>
            <dd class="text-heading tabular" :class="quietZero(summary.data.value.totals.notFound)" data-test="usage-not-found">{{ formatNumber(summary.data.value.totals.notFound) }}</dd>
          </div>
          <div>
            <dt class="text-small text-fg-2">{{ USAGE.ratings }}</dt>
            <dd class="text-heading tabular" data-test="usage-ratings">{{ USAGE.thumbs(formatNumber(summary.data.value.totals.thumbsUp), formatNumber(summary.data.value.totals.thumbsDown)) }}</dd>
          </div>
        </dl>
      </template>
    </AsyncState>
  </UiCard>
</template>
