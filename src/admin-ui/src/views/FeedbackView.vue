<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { AssistantSummary, FeedbackItem, FeedbackSummary } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import MetricTile from '@/components/ui/MetricTile.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import FilterChip from '@/components/ui/FilterChip.vue'
import PeriodPicker from '@/components/ui/PeriodPicker.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useKeysetList } from '@/composables/useKeysetList'
import { IS_DAY, IS_ID, ONE_OF, useQueryFilters } from '@/composables/useQueryFilters'
import { useAuthStore } from '@/stores/auth'
import { formatDateTime, formatNumber, formatRatio } from '@/utils/format'
import { FEEDBACK, FILTERS, PAGE } from '@/utils/labels'
import { PERIODS, periodRange } from '@/utils/period'
import { problemMessage } from '@/utils/problem'

// Feedback (admin design §8.1, chat design §14.4): ratings of the department's assistants without content (no
// question, answer or comment): rating, reason category, assistant and version, client, outcome, coverage, cited
// documents; totals for the period and import health per Open WebUI client. The page and its reads are the Feedback
// policy's (§3.3; the route's FEEDBACK_ROLES); FeedbackComments holders mark items handled.
// Without Open WebUI (`/bff/user` `openWebUi.enabled` false) nothing is imported: no import block, and the empty text
// names the API's feedback endpoint; feedback already stored still shows.
// `?documentId=` (Show the ratings on a knowledge base's Feedback on documents, §8.8): only the ratings whose answer cited
// that document, the caller's own assistants' as every row here.
// The filters are one row of chips kept in the address (§5.12 *Filters*): `?period=7d&rating=down&state=` and so on.
const auth = useAuthStore()
const summary = ref<FeedbackSummary | null>(null)
const assistants = ref<AssistantSummary[]>([])
const { filters, active, clear } = useQueryFilters({
  period: '30d', from: '', to: '', assistant: '', rating: '', outcome: '', comment: '', state: 'open', documentId: '',
}, {
  // A value outside a key's set reads as its default, so nothing the API would refuse is sent (#1051).
  period: ONE_OF(PERIODS), from: IS_DAY, to: IS_DAY, assistant: IS_ID, rating: ONE_OF(['up', 'down']),
  outcome: ONE_OF(Object.keys(FEEDBACK.outcomes)), comment: ONE_OF(['with', 'without']), state: ONE_OF(['all', 'reviewed', 'dismissed']),
  documentId: IS_ID,
})
const filterRow = ref<HTMLElement | null>(null)
/** The address says `up` or `down`; the API takes 1 or -1. */
const RATING_VALUE: Record<string, string> = { up: '1', down: '-1' }
const range = computed(() => periodRange(filters.period, filters.from, filters.to))
const busy = ref<string | null>(null)
const markError = ref<unknown>(null)
const canMark = computed(() => auth.can('feedback-comments'))

const columns = computed<Column[]>(() => [
  { key: 'time', label: FEEDBACK.time },
  { key: 'rating', label: FEEDBACK.rating },
  { key: 'assistant', label: FEEDBACK.assistant },
  { key: 'outcome', label: FEEDBACK.outcome },
  { key: 'cited', label: FEEDBACK.cited },
  { key: 'state', label: FEEDBACK.state },
  ...(canMark.value ? [{ key: 'actions', label: FEEDBACK.mark, hideLabel: true }] : []),
])
const assistantOptions = computed(() => assistants.value.map((a) => ({ value: a.id, label: a.name })))
const ratingOptions = [{ value: 'up', label: FEEDBACK.up }, { value: 'down', label: FEEDBACK.down }]
const outcomeOptions = Object.entries(FEEDBACK.outcomes).map(([value, label]) => ({ value, label }))
// §8.1's *has comment*: the comment is never shown, only whether there is one.
const commentOptions = [{ value: 'with', label: FEEDBACK.withComment }, { value: 'without', label: FEEDBACK.withoutComment }]
// Open is the default; the chip's other choices are every state, Handled and Dismissed.
const stateOptions = [{ value: 'all', label: FEEDBACK.allStates }, ...Object.entries(FEEDBACK.states).filter(([v]) => v !== 'open').map(([value, label]) => ({ value, label }))]

function day(value: string, next = false): string | undefined {
  if (!value) return undefined
  const date = new Date(`${value}T00:00:00`)
  if (Number.isNaN(date.getTime())) return undefined
  if (next) date.setDate(date.getDate() + 1)
  return date.toISOString()
}

const feedback = useKeysetList((after?: string) => api.feedback.list({
  from: day(range.value.from), to: day(range.value.to, true), assistantId: filters.assistant || undefined, rating: RATING_VALUE[filters.rating],
  outcome: filters.outcome || undefined, hasComment: filters.comment ? filters.comment === 'with' : undefined,
  state: filters.state === 'all' ? undefined : filters.state || undefined,
  documentId: filters.documentId || undefined, after, limit: 50,
}))

async function load(): Promise<void> {
  await feedback.reload()
  summary.value = await api.feedback.summary({ from: range.value.from || undefined, to: range.value.to || undefined, assistantId: filters.assistant || undefined })
    .catch(() => null)
}

async function mark(item: FeedbackItem, state: 'reviewed' | 'dismissed' | 'open'): Promise<void> {
  busy.value = item.id
  markError.value = null
  try {
    const saved = await api.feedback.setReviewState(item.id, state)
    item.reviewState = saved.reviewState
  } catch (e) {
    markError.value = e
  } finally {
    busy.value = null
  }
}

/** The filtered document's title, from a row that cited it; null before one has loaded. */
const citedTitle = computed(() =>
  feedback.items.value.flatMap((i) => i.citedDocuments).find((d) => d.id === filters.documentId)?.title ?? null)

function clearCited(): void {
  filters.documentId = ''
}

const totals = computed(() => summary.value?.totals ?? null)
const ratings = computed(() => (totals.value ? totals.value.thumbsUp + totals.value.thumbsDown : 0))

watch(() => ({ ...filters, ...range.value }), () => void load())
onMounted(async () => {
  await load()
  assistants.value = await api.assistants.list({ pageSize: 200 }).then((p) => p.items).catch(() => [])
})
</script>

<template>
  <PageHeader :title="PAGE.feedback.title" :description="FEEDBACK.lead" />

  <div v-if="totals" class="mb-6 grid gap-4" :class="auth.openWebUiEnabled ? 'md:grid-cols-3' : 'md:grid-cols-2'" data-test="totals">
    <MetricTile
      :label="FEEDBACK.answers" :value="formatNumber(totals.answers)"
      :context="FEEDBACK.notFound(formatNumber(totals.notFound), totals.answers ? formatRatio(totals.notFound / totals.answers) : null)"
    />
    <MetricTile :label="FEEDBACK.ratings" :value="formatNumber(ratings)" :context="FEEDBACK.thumbs(totals.thumbsUp, totals.thumbsDown)" />
    <UiCard v-if="auth.openWebUiEnabled" :title="FEEDBACK.imports" data-test="imports">
      <p v-if="!summary?.imports.length" class="text-small text-fg-2">{{ FEEDBACK.noImports }}</p>
      <ul v-else class="grid gap-1 text-small">
        <li v-for="i in summary.imports" :key="i.clientId">
          <span class="font-medium">{{ i.clientName }}</span>
          <UiBadge class="ml-1" :tone="i.alert ? 'danger' : i.lastOutcome === 'ok' ? 'ok' : i.lastOutcome ? 'warn' : 'neutral'" :label="FEEDBACK.importOutcome(i.lastOutcome)" />
          <span class="block text-fg-3">{{ FEEDBACK.importLine(formatDateTime(i.lastRunAt), i.unmapped) }}</span>
          <span v-if="i.alert" class="block text-danger" role="alert" data-test="import-alert">{{ FEEDBACK.importAlert(i.failedRuns) }}</span>
        </li>
      </ul>
    </UiCard>
  </div>

  <!-- One row of chips (admin design §5.12 Filters), kept in the address; the count at its end, politely announced. -->
  <form ref="filterRow" class="mb-6 flex flex-wrap items-center gap-2" role="search" :aria-label="FEEDBACK.filters" data-test="filters" @submit.prevent="load()">
    <PeriodPicker v-model:period="filters.period" v-model:from="filters.from" v-model:to="filters.to" id-prefix="feedback" />
    <FilterChip v-model="filters.assistant" :label="FEEDBACK.assistant" :options="assistantOptions" :any="FEEDBACK.allAssistants" />
    <FilterChip v-model="filters.rating" :label="FEEDBACK.rating" :options="ratingOptions" :any="FEEDBACK.allRatings" />
    <FilterChip v-model="filters.outcome" :label="FEEDBACK.outcome" :options="outcomeOptions" :any="FEEDBACK.allOutcomes" />
    <FilterChip v-model="filters.comment" :label="FEEDBACK.comment" :options="commentOptions" :any="FEEDBACK.anyComment" />
    <FilterChip v-model="filters.state" :label="FEEDBACK.state" :options="stateOptions" :any="FEEDBACK.states.open!" unset="open" />
    <UiButton v-if="active.length" size="sm" variant="quiet" class="max-md:h-11" data-test="clear-filters" @click="clear(filterRow)">{{ FILTERS.clear }}</UiButton>
    <p class="ml-auto text-small text-fg-2" aria-live="polite" data-test="filter-count">
      {{ feedback.loaded.value ? FEEDBACK.shown(feedback.items.value.length, !!feedback.next.value) : '' }}
    </p>
  </form>
  <p v-if="filters.documentId" class="mb-4 flex flex-wrap items-center gap-3" data-test="cited-filter">
    <span>{{ FEEDBACK.citedFilter(citedTitle) }}</span>
    <UiButton size="sm" variant="quiet" data-test="clear-cited" @click="clearCited()">{{ FEEDBACK.clearCited }}</UiButton>
  </p>
  <p class="mb-4 text-small text-fg-2">{{ FEEDBACK.noContent }}</p>
  <InlineError v-if="markError" :message="problemMessage(markError)" class="mb-4" />

  <AsyncState :loading="feedback.loading.value && feedback.items.value.length === 0" :error="feedback.error.value" :empty="feedback.items.value.length === 0" :empty-text="auth.openWebUiEnabled ? FEEDBACK.empty : FEEDBACK.emptyApi" @retry="load()">
    <DataTable :columns="columns" :rows="feedback.items.value" :row-key="(r) => r.id" :caption="FEEDBACK.caption">
      <template #cell-time="{ row }"><span class="tabular whitespace-nowrap">{{ formatDateTime(row.createdAt) }}</span></template>
      <template #cell-rating="{ row }">
        <span class="flex items-center gap-1" :data-test="`rating-${row.id}`">
          <UiBadge :tone="row.rating > 0 ? 'ok' : 'warn'" :label="row.rating > 0 ? FEEDBACK.up : FEEDBACK.down" />
          <span v-if="row.score" class="tabular text-small text-fg-2">{{ row.score }}/10</span>
        </span>
        <span v-if="row.reasonCategory" class="block text-small text-fg-2">{{ FEEDBACK.reasons[row.reasonCategory] ?? FEEDBACK.reasons.other }}</span>
      </template>
      <template #cell-assistant="{ row }">
        <span class="font-medium">{{ row.assistantName }}</span>
        <span v-if="row.assistantVersion" class="text-small text-fg-3"> · {{ FEEDBACK.version(row.assistantVersion) }}</span>
        <span v-if="row.clientName" class="block text-small text-fg-3">{{ row.clientName }}</span>
      </template>
      <template #cell-outcome="{ row }">
        {{ FEEDBACK.outcomes[row.outcome] ?? row.outcome }}
        <span v-if="row.coverage" class="block text-small text-fg-3">{{ FEEDBACK.coverage[row.coverage] ?? row.coverage }}</span>
      </template>
      <template #cell-cited="{ row }">
        <ul v-if="row.citedDocuments.length" class="grid gap-0.5 text-small">
          <li v-for="d in row.citedDocuments" :key="d.id" class="break-all">{{ d.title ?? FEEDBACK.untitled }}</li>
        </ul>
        <span v-else class="text-fg-3">–</span>
      </template>
      <template #cell-state="{ row }">
        <UiBadge :tone="row.reviewState === 'open' ? 'accent' : 'neutral'" :label="FEEDBACK.states[row.reviewState] ?? row.reviewState" />
      </template>
      <template #cell-actions="{ row }">
        <span class="flex justify-end gap-2">
          <UiButton v-if="row.reviewState === 'open'" size="sm" :loading="busy === row.id" data-test="mark" @click="mark(row, 'reviewed')">{{ FEEDBACK.mark }}</UiButton>
          <UiButton v-else size="sm" variant="quiet" :loading="busy === row.id" data-test="reopen" @click="mark(row, 'open')">{{ FEEDBACK.reopen }}</UiButton>
        </span>
      </template>
    </DataTable>
    <UiButton v-if="feedback.next.value" class="mt-4" :loading="feedback.loading.value" data-test="more" @click="feedback.more()">{{ FEEDBACK.more }}</UiButton>
  </AsyncState>
  <!-- The endpoint is technical text: behind Details, not in the sentence (admin design §5.7). -->
  <details
    v-if="!auth.openWebUiEnabled && feedback.loaded.value && !feedback.error.value && feedback.items.value.length === 0" class="mx-auto max-w-[680px] text-center text-small text-fg-2"
    data-test="feedback-api-details"
  >
    <summary class="cursor-pointer">{{ FEEDBACK.emptyApiDetails }}</summary>
    <p class="mt-2">{{ FEEDBACK.emptyApiEndpoint }}</p>
  </details>
</template>
