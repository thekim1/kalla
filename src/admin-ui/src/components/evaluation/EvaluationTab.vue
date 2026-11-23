<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { EvaluationOverview, EvaluationRun, GenerationForecast } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import UiDialog from '@/components/ui/UiDialog.vue'
import { useJobPoll } from '@/composables/useJobPoll'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatDecimal, formatNumber, formatUsdCost } from '@/utils/format'
import { COMMON, EVALUATION } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import EvaluationQuestions from './EvaluationQuestions.vue'
import EvaluationReport from './EvaluationReport.vue'

// Testfrågor och kvalitet (admin design §7.1): the set by layer with Skapa fler and its forecast, Kör utvärdering, the
// latest report (§7.2), earlier runs as a table (the trend's table view), and the questions.
const props = defineProps<{ collectionId: string; canEdit: boolean }>()
const emit = defineEmits<{ (e: 'use-proposal', values: { coverageMaxDistance?: number; coverageMinKeywordScore?: number }): void }>()
const ui = useUiStore()
const overview = useLoadable<EvaluationOverview | null>(null)
const compared = ref<EvaluationRun | null>(null)
const runPoll = useJobPoll<unknown>()
const runError = ref<unknown>(null)
const starting = ref(false)
const generateOpen = ref(false)
const forecast = ref<GenerationForecast | null>(null)
const forecastError = ref<unknown>(null)
const generating = ref(false)
const questions = ref<InstanceType<typeof EvaluationQuestions> | null>(null)

const counts = computed(() => {
  const by: Record<string, { active: number; stale: number; retired: number }> = {}
  for (const c of overview.data.value?.questions ?? []) {
    const layer = (by[c.layer] ??= { active: 0, stale: 0, retired: 0 })
    if (c.state === 'active' || c.state === 'stale' || c.state === 'retired') layer[c.state] += c.count
  }
  return by
})
const runs = computed(() => overview.data.value?.latestRuns ?? [])
/** The report leads with the latest finished run under the saved settings, hybrid first (§7.2). */
const report = computed(() => {
  const finished = runs.value.filter((r) => r.finishedAt && !r.candidate)
  return finished.find((r) => r.variant === 'hybrid') ?? finished[0] ?? null
})
const hasGenerated = computed(() => (counts.value.generated?.active ?? 0) + (counts.value.generated?.stale ?? 0) > 0)
const historyColumns: Column[] = [
  { key: 'started', label: EVALUATION.started },
  { key: 'kind', label: EVALUATION.kind },
  { key: 'count', label: EVALUATION.questionsCount, align: 'right' },
  { key: 'recall', label: EVALUATION.recall, align: 'right' },
  { key: 'mrr', label: EVALUATION.mrr, align: 'right' },
]
const decimals = (v: number | null) => (v == null ? '–' : formatDecimal(v, 2, 2))

async function load(): Promise<void> {
  await overview.run(() => api.collections.evaluation(props.collectionId))
}

watch(report, async (run) => {
  compared.value = null
  if (!run?.comparedRunId) return
  compared.value = runs.value.find((r) => r.id === run.comparedRunId) ?? (await api.evaluation.getRun(run.comparedRunId).catch(() => null))
})

async function runNow(): Promise<void> {
  starting.value = true
  runError.value = null
  try {
    const started = await api.evaluation.run(props.collectionId)
    if (started.alreadyQueued) ui.notify(EVALUATION.runQueued)
    runPoll.start(started.jobId)
  } catch (e) {
    runError.value = e
  } finally {
    starting.value = false
  }
}

// The run's end, or a failed request about it (the job then unknown).
watch(
  () => (runPoll.failed.value ? 'failed' : runPoll.job.value?.state),
  (state) => {
    if (!state || runPoll.polling.value) return
    ui.notify(state === 'succeeded' ? EVALUATION.runDone : EVALUATION.runFailed)
    void load()
  },
)

async function openGenerate(): Promise<void> {
  generateOpen.value = true
  forecast.value = null
  forecastError.value = null
  try {
    forecast.value = await api.evaluation.forecast(props.collectionId)
  } catch (e) {
    forecastError.value = e
  }
}

async function generate(): Promise<void> {
  generating.value = true
  forecastError.value = null
  try {
    await api.evaluation.generate(props.collectionId)
    generateOpen.value = false
    ui.notify(EVALUATION.generating)
  } catch (e) {
    forecastError.value = e
  } finally {
    generating.value = false
  }
}

function questionsChanged(): void {
  void load()
  // The worker applies edit-eval-question shortly after; refresh the table then.
  setTimeout(() => questions.value?.reload(), 3000)
}

onMounted(load)
</script>

<template>
  <AsyncState :loading="overview.loading.value && !overview.data.value" :error="overview.error.value" @retry="load">
    <div v-if="overview.data.value" class="grid gap-6">
      <UiCard :title="EVALUATION.set" data-test="set">
        <template #actions>
          <span v-if="canEdit" class="flex flex-wrap gap-2">
            <UiButton size="sm" data-test="generate" @click="openGenerate">{{ hasGenerated ? EVALUATION.generate : EVALUATION.generateFirst }}</UiButton>
            <UiButton
              v-if="overview.data.value.hasSet" size="sm" variant="primary" :loading="starting || runPoll.polling.value" data-test="run" @click="runNow"
            >
              {{ runPoll.polling.value ? EVALUATION.running : EVALUATION.run }}
            </UiButton>
          </span>
        </template>
        <p v-if="!overview.data.value.hasSet" class="text-fg-2">{{ EVALUATION.noSet }}</p>
        <dl v-else class="grid gap-1" data-test="layers">
          <div v-for="l in ['human', 'generated']" :key="l" class="flex flex-wrap gap-x-2">
            <dt class="font-medium">{{ EVALUATION.layers[l] }}</dt>
            <dd class="tabular text-fg-2">{{ EVALUATION.layerCount(counts[l]?.active ?? 0, counts[l]?.stale ?? 0, counts[l]?.retired ?? 0) }}</dd>
          </div>
        </dl>
        <InlineError v-if="runError" :message="problemMessage(runError)" class="mt-3" />
      </UiCard>

      <EvaluationReport v-if="report" :run="report" :compared="compared" :can-edit="canEdit" @use-proposal="emit('use-proposal', $event)" />
      <UiCard v-else :title="EVALUATION.report"><p class="text-fg-2">{{ EVALUATION.noReport }}</p></UiCard>

      <UiCard v-if="runs.length > 1" :title="EVALUATION.history" data-test="history">
        <DataTable :columns="historyColumns" :rows="runs" :row-key="(r) => r.id" :caption="EVALUATION.historyCaption">
          <template #cell-started="{ row }"><span class="tabular">{{ formatDateTime(row.startedAt) }}</span></template>
          <template #cell-kind="{ row }">
            {{ EVALUATION.kinds[row.kind] ?? row.kind }}
            <UiBadge v-if="row.candidate" :label="EVALUATION.candidate" class="ml-1" />
            <UiBadge v-if="!row.finishedAt" tone="accent" :label="EVALUATION.unfinished" class="ml-1" />
          </template>
          <template #cell-count="{ row }"><span class="tabular">{{ formatNumber(row.questionCount) }}</span></template>
          <template #cell-recall="{ row }"><span class="tabular">{{ decimals(row.recallAtK) }}</span></template>
          <template #cell-mrr="{ row }"><span class="tabular">{{ decimals(row.mrr) }}</span></template>
        </DataTable>
      </UiCard>

      <EvaluationQuestions
        ref="questions" :collection-id="collectionId" :can-edit="canEdit" :deleted="counts.human?.stale ?? 0" @changed="questionsChanged"
      />
    </div>
  </AsyncState>

  <UiDialog :open="generateOpen" :title="EVALUATION.generateTitle" @update:open="generateOpen = $event">
    <div v-if="forecast" class="grid gap-2" data-test="forecast">
      <p v-if="forecast.needed > 0">{{ EVALUATION.forecast(forecast.needed, forecast.target, forecast.modelCalls) }}</p>
      <p v-else>{{ EVALUATION.forecastEnough(forecast.target) }}</p>
      <p v-if="forecast.needed > 0" class="text-fg-2">
        {{ forecast.estimatedCostUsd != null
          ? EVALUATION.forecastCost(formatNumber(forecast.estimatedTokens), formatUsdCost(forecast.estimatedCostUsd))
          : EVALUATION.forecastTokens(formatNumber(forecast.estimatedTokens)) }}
      </p>
      <p v-if="forecast.model" class="text-small text-fg-3">{{ EVALUATION.forecastModel(forecast.model) }}</p>
      <p v-if="forecast.kind === 'top-up-set'" class="text-small text-fg-3">{{ EVALUATION.forecastTopUp }}</p>
    </div>
    <p v-else-if="!forecastError">{{ COMMON.loading }}</p>
    <InlineError v-if="forecastError" :message="problemMessage(forecastError)" />
    <template #actions>
      <UiButton @click="generateOpen = false">{{ COMMON.cancel }}</UiButton>
      <UiButton variant="primary" :disabled="!forecast || forecast.needed === 0" :loading="generating" data-test="confirm-generate" @click="generate">
        {{ hasGenerated ? EVALUATION.generate : EVALUATION.generateFirst }}
      </UiButton>
    </template>
  </UiDialog>
</template>
