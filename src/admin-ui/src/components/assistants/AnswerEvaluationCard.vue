<script setup lang="ts">
import { computed, watch } from 'vue'
import { api } from '@/api'
import type { AnswerEvaluation, AnswerRunSummary } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useLoadable } from '@/stores/loadable'
import { formatDate, formatNumber, formatRatio } from '@/utils/format'
import { ANSWER_EVALUATION as L } from '@/utils/labels'

// The Statistics tab's latest answer evaluation (admin design §5.5.4, processing §14.8; B-58): faithfulness and citation
// precision of the last finished nightly run with its counts, the run it compares with, and the live rate of answers without a
// citation. While the run is switched off (Settings, Evaluation) the card says so and keeps the last run's figures.
const props = defineProps<{ assistantId: string }>()
const evaluation = useLoadable<AnswerEvaluation | null>(null)

async function load(): Promise<void> {
  await evaluation.run(() => api.assistantTests.answerEvaluation(props.assistantId))
}

watch(() => props.assistantId, load, { immediate: true })

const latest = computed(() => evaluation.data.value?.latest ?? null)
const previous = computed(() => evaluation.data.value?.previous ?? null)

/** A figure with the previous run's beside it, when there is one. */
function figure(pick: (run: AnswerRunSummary) => number | null): { value: string; before: string | null } {
  const now = latest.value ? formatRatio(pick(latest.value)) : ''
  const before = previous.value ? L.comparedTo(formatRatio(pick(previous.value)), formatDate(previous.value.startedAt)) : null
  return { value: now, before }
}

const faithfulness = computed(() => figure((r) => r.faithfulness))
const precision = computed(() => figure((r) => r.citationPrecision))
</script>

<template>
  <UiCard :title="L.title" data-test="answer-evaluation">
    <AsyncState :loading="evaluation.loading.value" :error="evaluation.error.value" :empty="!evaluation.data.value" @retry="load()">
      <template v-if="evaluation.data.value">
        <p class="mb-3 text-small text-fg-2">{{ L.lead }}</p>
        <p v-if="!evaluation.data.value.enabled" class="mb-3 text-small" role="note" data-test="answer-evaluation-off">
          {{ latest ? L.off : L.offNoRun }}
        </p>
        <p v-if="!latest && evaluation.data.value.enabled" class="mb-3 text-small text-fg-2" data-test="answer-evaluation-none">{{ L.noRun }}</p>
        <template v-if="latest">
          <p class="mb-3 text-small text-fg-2" data-test="answer-evaluation-date">{{ L.ranOn(formatDate(latest.startedAt), latest.version) }}</p>
          <dl class="mb-4 grid gap-4 sm:grid-cols-2">
            <div>
              <dt class="text-small text-fg-2" :title="L.faithfulnessHint">{{ L.faithfulness }}</dt>
              <dd class="text-heading tabular" data-test="answer-evaluation-faithfulness">{{ faithfulness.value }}</dd>
              <dd v-if="faithfulness.before" class="text-small text-fg-3">{{ faithfulness.before }}</dd>
              <dd class="text-small text-fg-3">{{ L.faithfulnessHint }}</dd>
            </div>
            <div>
              <dt class="text-small text-fg-2" :title="L.citationPrecisionHint">{{ L.citationPrecision }}</dt>
              <dd class="text-heading tabular" data-test="answer-evaluation-precision">{{ precision.value }}</dd>
              <dd v-if="precision.before" class="text-small text-fg-3">{{ precision.before }}</dd>
              <dd class="text-small text-fg-3">{{ L.citationPrecisionHint }}</dd>
            </div>
            <div>
              <dt class="text-small text-fg-2">{{ L.questions }}</dt>
              <dd class="tabular" data-test="answer-evaluation-questions">{{ formatNumber(latest.questions) }}</dd>
            </div>
            <div>
              <dt class="text-small text-fg-2">{{ L.found }}</dt>
              <dd class="tabular" data-test="answer-evaluation-found">{{ formatNumber(latest.found) }}</dd>
            </div>
          </dl>
          <p v-if="latest.wins !== null && latest.losses !== null" class="mb-4 text-small text-fg-2" data-test="answer-evaluation-changes">
            {{ L.changes(latest.wins, latest.losses) }}
          </p>
        </template>
        <dl>
          <dt class="text-small text-fg-2">{{ L.uncited }}</dt>
          <dd class="tabular" data-test="answer-evaluation-uncited">
            {{
              evaluation.data.value.live.answered > 0
                ? L.uncitedValue(formatRatio(evaluation.data.value.live.rate), evaluation.data.value.live.uncited, evaluation.data.value.live.answered,
                                 evaluation.data.value.live.days)
                : L.uncitedNone(evaluation.data.value.live.days)
            }}
          </dd>
        </dl>
      </template>
    </AsyncState>
  </UiCard>
</template>
