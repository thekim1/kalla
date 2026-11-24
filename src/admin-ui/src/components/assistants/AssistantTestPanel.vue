<script setup lang="ts">
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { formatDecimal, formatNumber } from '@/utils/format'
import { ASSISTANT, ASSISTANT_TESTS } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import type { AssistantTest } from './useAssistantTest'

/**
 * The test panel (admin design §4.9, §5.5.4 *Build*): a question on the draft (the published version when there is no
 * draft) with the general-knowledge level to test, the answer as plain text with its outcome, the model, time, cost and
 * length limit sent, the sources searched with the cited ones marked, and *Save as test question*. Nothing is stored
 * but the usage. The state is the page's (`useAssistantTest`), so the panel can move without losing the answer.
 */
const props = defineProps<{ test: AssistantTest; canEdit: boolean }>()
const emit = defineEmits<{ (e: 'save-question', question: string): void }>()
const { question, level, levelOptions, result, busy, error, ask } = props.test
const cost = (usd: number) => (usd > 0 && usd < 0.00005 ? ASSISTANT.costBelow(formatDecimal(0.0001, 4, 4)) : formatDecimal(usd, 4, 4))
</script>

<template>
  <div class="grid gap-5" data-test="test-panel">
    <form class="grid gap-3" @submit.prevent="ask">
      <TextAreaField id="assistant-question" v-model="question" :label="ASSISTANT.question" :hint="ASSISTANT.askHint" :rows="2" />
      <SelectField id="assistant-test-general-knowledge"
        v-model="level" class="max-w-sm" :label="ASSISTANT.testGeneralKnowledge" :hint="ASSISTANT.testGeneralKnowledgeHint"
        :options="levelOptions"
      />
      <div><UiButton type="submit" variant="primary" :loading="busy" :disabled="!question.trim()" data-test="ask">{{ ASSISTANT.ask }}</UiButton></div>
    </form>
    <InlineError v-if="error" :message="problemMessage(error)" />
    <template v-if="result">
      <section class="grid gap-1" aria-labelledby="assistant-test-answer" data-test="answer">
        <h3 id="assistant-test-answer" class="font-medium">{{ ASSISTANT.answer }}</h3>
        <div><UiBadge :label="ASSISTANT.outcome[result.answer.outcome.replace('-', '')] ?? result.answer.outcome" class="mb-2" /></div>
        <p class="whitespace-pre-wrap">{{ result.answer.delivered }}</p>
        <details v-if="result.answer.raw" class="mt-2"><summary class="cursor-pointer text-small">{{ ASSISTANT.raw }}</summary><p class="whitespace-pre-wrap text-small text-fg-2">{{ result.answer.raw }}</p></details>
        <p class="mt-2 text-small text-fg-3" data-test="test-general-knowledge">
          {{ ASSISTANT.testGeneralKnowledgeRan(ASSISTANT.generalKnowledgeNames[result.generalKnowledge] ?? result.generalKnowledge) }}
        </p>
        <p class="text-small text-fg-3">{{ ASSISTANT.gateway(result.gateway.model, result.gateway.residency, String(Math.round(result.durationMs))) }}</p>
        <p v-if="result.gateway.maxTokensSource" class="text-small text-fg-3" data-test="max-tokens-sent">
          {{ ASSISTANT.maxTokensSent(result.gateway.maxTokens == null ? null : formatNumber(result.gateway.maxTokens),
                                     ASSISTANT.maxTokensSources[result.gateway.maxTokensSource] ?? result.gateway.maxTokensSource) }}
        </p>
        <p class="text-small text-fg-3" data-test="test-cost">{{ ASSISTANT.testCost(cost(result.gateway.costUsd), result.gateway.costEstimated) }}</p>
        <p v-if="result.gateway.finishReason === 'length'" role="status" class="mt-2 font-medium text-warn" data-test="length-stopped">
          {{ ASSISTANT.lengthStopped }}
        </p>
        <div v-if="canEdit" class="mt-2">
          <UiButton size="sm" icon="add" data-test="save-test-question" @click="emit('save-question', question)">{{ ASSISTANT_TESTS.saveFromTest }}</UiButton>
        </div>
      </section>
      <section class="grid gap-2" aria-labelledby="assistant-test-sources">
        <h3 id="assistant-test-sources" class="font-medium">{{ ASSISTANT.sources }}</h3>
        <ol class="grid gap-3">
          <li v-for="c in result.retrieval.candidates" :key="c.rank">
            <span class="font-medium">{{ c.rank }}. {{ c.title }}</span>
            <UiBadge v-if="c.cited" tone="ok" :label="ASSISTANT.cited" class="ml-2" />
            <span class="block text-small text-fg-3">{{ c.headingPath }}</span>
            <span class="block text-small text-fg-2">{{ c.text }}</span>
          </li>
        </ol>
      </section>
    </template>
  </div>
</template>
