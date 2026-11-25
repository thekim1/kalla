<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import { api } from '@/api'
import type { EvaluationTrendRun } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import UiCard from '@/components/ui/UiCard.vue'
import TimeSeriesChart from '@/components/usage/TimeSeriesChart.vue'
import { useLoadable } from '@/stores/loadable'
import { formatDateTime, formatDecimal } from '@/utils/format'
import { EVALUATION } from '@/utils/labels'
import { trendChart, trendLayers } from '@/utils/evaluationTrend'

// The trend (admin design §7.1, B-69): recall@10 and MRR of the latest 30 finished runs as lines, one per metric and
// layer with the `human` layer first (TimeSeriesChart, copied from the gateway), and under it the table of the same runs
// as its text view (§10.1), newest first. `refresh` changes when the tab reads its runs again (a run has finished).
const props = defineProps<{ collectionId: string; refresh?: string }>()
const trend = useLoadable<EvaluationTrendRun[]>([])

const runs = computed(() => trend.data.value)
const chart = computed(() => trendChart(runs.value))
const layers = computed(() => trendLayers(runs.value))
const newestFirst = computed(() => [...runs.value].reverse())
const columns = computed<Column[]>(() => [
  { key: 'started', label: EVALUATION.started },
  { key: 'kind', label: EVALUATION.kind },
  ...layers.value.flatMap((layer) => {
    const name = EVALUATION.layers[layer] ?? layer
    return [
      { key: `${layer}-recall`, label: EVALUATION.trendSeries(name, EVALUATION.recall), align: 'right' as const },
      { key: `${layer}-mrr`, label: EVALUATION.trendSeries(name, EVALUATION.mrr), align: 'right' as const },
    ]
  }),
])
const decimals = (v: number | null | undefined) => (v == null ? '–' : formatDecimal(v, 2, 2))
const cell = (run: EvaluationTrendRun, key: string) => {
  const [layer, metric] = [key.slice(0, key.lastIndexOf('-')), key.slice(key.lastIndexOf('-') + 1)]
  const found = run.layers.find((l) => l.layer === layer)
  return decimals(found ? (metric === 'recall' ? found.recallAtK : found.mrr) : null)
}
const slotOf = (ms: number) => formatDateTime(new Date(ms).toISOString())

async function load(): Promise<void> {
  await trend.run(async () => (await api.collections.evaluationTrend(props.collectionId)).runs)
}
onMounted(load)
watch(() => props.refresh, () => void load())
</script>

<template>
  <AsyncState :loading="trend.loading.value && runs.length === 0" :error="trend.error.value" @retry="load">
    <UiCard v-if="runs.length > 1" :title="EVALUATION.trend" data-test="trend">
      <p class="mb-4 text-small text-fg-2">{{ EVALUATION.trendLead }}</p>
      <TimeSeriesChart
        :label="EVALUATION.trendLabel" :slots="chart.slots" :series="chart.series" kind="line" :format="decimals" :format-slot="slotOf"
        data-test="trend-chart"
      />
      <ul class="my-4 flex flex-wrap gap-x-4 gap-y-1 text-small text-fg-2" aria-hidden="true" data-test="trend-legend">
        <li v-for="s in chart.series" :key="s.id" class="inline-flex items-center gap-1.5">
          <span class="inline-block size-2 rounded-full" :style="{ background: s.color }" />{{ s.name }}
        </li>
      </ul>
      <DataTable :columns="columns" :rows="newestFirst" :row-key="(r) => r.id" :caption="EVALUATION.historyCaption" data-test="trend-table">
        <template #cell-started="{ row }"><span class="tabular">{{ formatDateTime(row.startedAt) }}</span></template>
        <template #cell-kind="{ row }">{{ EVALUATION.kinds[row.kind] ?? row.kind }}</template>
        <template v-for="c in columns.slice(2)" :key="c.key" #[`cell-${c.key}`]="{ row }"><span class="tabular">{{ cell(row as EvaluationTrendRun, c.key) }}</span></template>
      </DataTable>
    </UiCard>
  </AsyncState>
</template>
