<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { EvaluationResult, EvaluationRun } from '@/api/types'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { formatDateTime, formatDecimal, formatRatio } from '@/utils/format'
import { EVALUATION } from '@/utils/labels'
import { layerMetrics, rankChanges, type LayerMetrics } from './metrics'

// The evaluation report card (admin design §7.2): led by the human layer (the gold set: recall@10 on answerable
// questions, false not-found, unanswerable answered "not found"; weakness review §2), then the generated layer as a
// regression signal, the questions that got worse or better, "Om utvärderingen", and the coverage-threshold proposal
// as numbers (§7.1), which the editor takes to Sökinställningar. Questions whose expected document was deleted are left
// out of the run and counted apart (`documentGone`, processing design §13.3).
const props = defineProps<{ run: EvaluationRun; compared: EvaluationRun | null; canEdit?: boolean }>()
const emit = defineEmits<{ (e: 'use-proposal', values: { coverageMaxDistance?: number; coverageMinKeywordScore?: number }): void }>()

/** Below this many answerable questions a comparison is noise (§7.2). */
const MIN_COMPARABLE = 20

const results = ref<EvaluationResult[]>([])
const previous = ref<EvaluationResult[] | null>(null)
const showAll = ref(false)

async function load(): Promise<void> {
  const [now, before] = await Promise.all([
    api.evaluation.results(props.run.id).catch(() => [] as EvaluationResult[]),
    props.compared ? api.evaluation.results(props.compared.id).catch(() => null) : Promise.resolve(null),
  ])
  results.value = now
  previous.value = before
}
watch(() => [props.run.id, props.compared?.id], load, { immediate: true })

const human = computed(() => layerMetrics(results.value, 'human', props.run.k))
const humanBefore = computed(() => (previous.value ? layerMetrics(previous.value, 'human', props.run.k) : null))
const generated = computed(() => layerMetrics(results.value, 'generated', props.run.k))
const generatedBefore = computed(() => (previous.value ? layerMetrics(previous.value, 'generated', props.run.k) : null))
const changes = computed(() => rankChanges(results.value, previous.value))
const answerable = computed(() => human.value.answerable + generated.value.answerable)
const proposal = computed(() => props.run.thresholdProposal)
const proposed = computed(() => ({
  coverageMaxDistance: proposal.value?.coverageMaxDistance?.value,
  coverageMinKeywordScore: proposal.value?.coverageMinKeywordScore?.value,
}))
const hasProposal = computed(() => proposed.value.coverageMaxDistance != null || proposed.value.coverageMinKeywordScore != null)

const decimals = (v: number | null | undefined) =>
  v == null ? '–' : formatDecimal(v, 2, 2)
const share = (part: number | undefined, whole: number | undefined) => (whole ? formatRatio((part ?? 0) / whole) : '–')
function line(now: LayerMetrics, before: LayerMetrics | null, key: 'recall' | 'mrr'): string {
  return before && before.answerable > 0 ? EVALUATION.change(decimals(before[key]), decimals(now[key])) : decimals(now[key])
}
const rank = (r: number | null) => (r == null ? EVALUATION.notFoundRank : String(r))
const title = computed(() => {
  const head = EVALUATION.reportTitle(EVALUATION.kinds[props.run.kind] ?? props.run.kind, formatDateTime(props.run.startedAt))
  return props.compared ? `${head} (${EVALUATION.comparedWith(formatDateTime(props.compared.startedAt))})` : head
})
const resultColumns: Column[] = [
  { key: 'question', label: EVALUATION.question },
  { key: 'layer', label: EVALUATION.layer },
  { key: 'rank', label: EVALUATION.rank, align: 'right' },
  { key: 'previousRank', label: EVALUATION.previousRank, align: 'right' },
  { key: 'coverage', label: EVALUATION.coverage },
]
</script>

<template>
  <UiCard :title="EVALUATION.report" data-test="report">
    <p class="mb-4 font-medium">{{ title }}</p>

    <div v-if="human.questions > 0" class="mb-4" data-test="human">
      <h3 class="mb-1 font-medium">{{ EVALUATION.humanLayer(human.questions) }}</h3>
      <dl class="flex flex-wrap gap-x-6 gap-y-1 text-small">
        <div><dt class="inline text-fg-2">{{ EVALUATION.recall }}</dt> <dd class="tabular inline">{{ line(human, humanBefore, 'recall') }}</dd></div>
        <div v-if="proposal">
          <dt class="inline text-fg-2">{{ EVALUATION.falseNotFound }}</dt> <dd class="tabular inline">{{ share(proposal.falseNotFound, proposal.answerable) }}</dd>
        </div>
        <div v-if="proposal && proposal.unanswerable > 0">
          <dt class="inline text-fg-2">{{ EVALUATION.unanswerableNotFound }}</dt>
          <dd class="tabular inline">{{ share(proposal.unanswerableNotFound, proposal.unanswerable) }}</dd>
        </div>
      </dl>
    </div>

    <div v-if="generated.questions > 0" class="mb-4" data-test="generated">
      <h3 class="mb-1 font-medium">{{ EVALUATION.generatedLayer }}</h3>
      <dl class="flex flex-wrap gap-x-6 gap-y-1 text-small">
        <div><dt class="inline text-fg-2">{{ EVALUATION.recall }}</dt> <dd class="tabular inline">{{ line(generated, generatedBefore, 'recall') }}</dd></div>
        <div><dt class="inline text-fg-2">{{ EVALUATION.mrr }}</dt> <dd class="tabular inline">{{ line(generated, generatedBefore, 'mrr') }}</dd></div>
      </dl>
    </div>

    <p class="mb-3 text-small text-fg-2" data-test="summary">
      <template v-if="answerable < MIN_COMPARABLE">{{ EVALUATION.tooFew }}</template>
      <template v-else>{{ EVALUATION.summary(results.length, changes.worse.length, changes.better.length) }}</template>
    </p>
    <p v-if="run.documentGone" class="mb-3 text-small text-fg-2" data-test="document-gone">{{ EVALUATION.documentGone(run.documentGone) }}</p>

    <details v-if="changes.worse.length" class="mb-2">
      <summary class="cursor-pointer">{{ EVALUATION.worse(changes.worse.length) }}</summary>
      <ul class="mt-2 grid gap-1 text-small" data-test="worse">
        <li v-for="r in changes.worse" :key="r.questionId">{{ r.question }} · {{ EVALUATION.rankChange(rank(r.previousRank), rank(r.rank)) }}</li>
      </ul>
    </details>
    <details v-if="changes.better.length" class="mb-2">
      <summary class="cursor-pointer">{{ EVALUATION.better(changes.better.length) }}</summary>
      <ul class="mt-2 grid gap-1 text-small" data-test="better">
        <li v-for="r in changes.better" :key="r.questionId">{{ r.question }} · {{ EVALUATION.rankChange(rank(r.previousRank), rank(r.rank)) }}</li>
      </ul>
    </details>
    <details class="mb-4">
      <summary class="cursor-pointer">{{ EVALUATION.about }}</summary>
      <p class="mt-2 text-small text-fg-2">{{ EVALUATION.aboutText }}</p>
    </details>

    <div v-if="hasProposal" class="mb-4 rounded-tile border border-border p-4" data-test="proposal">
      <h3 class="mb-1 font-medium">{{ EVALUATION.proposal }}</h3>
      <p class="mb-2 text-small text-fg-2">{{ EVALUATION.proposalLead }}</p>
      <ul class="mb-3 grid gap-1 text-small">
        <li v-if="proposal?.coverageMaxDistance">
          {{ EVALUATION.proposalDistance(decimals(proposal.coverageMaxDistance.value), proposal.coverageMaxDistance.misclassified) }}
        </li>
        <li v-if="proposal?.coverageMinKeywordScore">
          {{ EVALUATION.proposalKeyword(decimals(proposal.coverageMinKeywordScore.value), proposal.coverageMinKeywordScore.misclassified) }}
        </li>
      </ul>
      <UiButton v-if="canEdit" size="sm" data-test="use-proposal" @click="emit('use-proposal', proposed)">{{ EVALUATION.useProposal }}</UiButton>
    </div>

    <UiButton v-if="results.length" size="sm" data-test="toggle-results" @click="showAll = !showAll">
      {{ showAll ? EVALUATION.hideResults : EVALUATION.showResults }}
    </UiButton>
    <DataTable v-if="showAll" class="mt-3" :columns="resultColumns" :rows="results" :row-key="(r) => r.questionId" :caption="EVALUATION.resultsCaption">
      <template #cell-layer="{ row }">{{ EVALUATION.layers[row.layer] ?? row.layer }}</template>
      <template #cell-rank="{ row }"><span class="tabular">{{ row.hit === null ? '–' : rank(row.rank) }}</span></template>
      <template #cell-previousRank="{ row }"><span class="tabular">{{ row.hit === null || !compared ? '–' : rank(row.previousRank) }}</span></template>
      <template #cell-coverage="{ row }">{{ row.coverage ? EVALUATION.coverageStates[row.coverage] ?? row.coverage : '–' }}</template>
    </DataTable>
  </UiCard>
</template>
