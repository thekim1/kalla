<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { Run, RunItem } from '@/api/types'
import CancelRunButton from '@/components/runs/CancelRunButton.vue'
import DeletionDecision from '@/components/sources/DeletionDecision.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import MetricTile from '@/components/ui/MetricTile.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { pollDelay } from '@/composables/useJobPoll'
import { reportDifferences } from '@/platform/politeness'
import { useLoadable } from '@/stores/loadable'
import { formatDateTime, formatNumber } from '@/utils/format'
import { PLATFORM, RUN_OUTCOME_LABEL, RUN_PAGE, RUN_PROGRESS, RUN_STATE_LABEL } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'
import { runProgressText } from '@/utils/runs'

// Körning (admin design §5.5.3, §5.4 step 6; #278 slice 2): live counters and progress while it runs (polled as §4.7,
// derived from the items so far, #803; the documents table refreshes with them until the user pages), the crawl
// report (completeness and its reasons, the verdict, the websites fetched with their own politeness, connectors design
// §6.6, and for a browser source the rendered pages, those with no main text, render failures and pages kept from their
// last good content, §8.6), the deletion decision when the guard held deletions back, and the run's documents with their
// outcome and reason. Cancel run while it is running or waiting for the deletion decision (#488 part 3).
const props = defineProps<{ id: string }>()
const run = useLoadable<Run | null>(null)
const items = useLoadable<RunItem[]>([])
const next = ref<number | null>(null)
const onlyFailed = ref(false)
// The user asked for more rows: a running poll no longer replaces the table's first page.
const paged = ref(false)
let timer: ReturnType<typeof setTimeout> | null = null
const started = Date.now()

const columns: Column[] = [
  { key: 'identity', label: RUN_PAGE.document },
  { key: 'outcome', label: RUN_PAGE.outcome },
  { key: 'reason', label: RUN_PAGE.reason },
]
const summary = computed(() => run.data.value?.run)
const progress = computed(() => runProgressText(summary.value))
const completeness = computed(() => run.data.value?.report?.completeness)
// "www.umea.se: Fast · 20 requests a second · robots.txt ignored: own site, web team consent"
const politeness = computed(() =>
  (run.data.value?.report?.politeness ?? []).map((p) => ({
    host: p.host,
    text: [
      PLATFORM.politeness.presets[p.preset] ?? p.preset,
      ...reportDifferences(p),
      ...(p.robotsIgnoredReason ? [RUN_PAGE.robotsIgnored(p.robotsIgnoredReason)] : []),
    ].join(' · '),
  })),
)
const render = computed(() => run.data.value?.report?.render)
const verdictReasons = computed(() =>
  [run.data.value?.report?.verdict?.runFailure, run.data.value?.report?.verdict?.runDegradation].filter((r): r is string => !!r),
)

async function loadItems(append = false): Promise<void> {
  if (append) paged.value = true
  const previous = append ? items.data.value : []
  await items.run(async () => {
    const page = await api.runs.items(props.id, { outcome: onlyFailed.value ? 'failed' : undefined, after: append ? next.value ?? undefined : undefined, limit: 50 })
    next.value = page.next
    return [...previous, ...page.items]
  })
}

async function load(): Promise<void> {
  // A reload after Cancel run replaces the running poll.
  if (timer) clearTimeout(timer)
  timer = null
  await run.run(() => api.runs.get(props.id))
  if (summary.value?.state === 'running') {
    timer = setTimeout(() => void load(), pollDelay(Date.now() - started))
    if (!paged.value) void loadItems()
  } else void loadItems()
}

watch(onlyFailed, () => {
  paged.value = false
  void loadItems()
})
onMounted(load)
onBeforeUnmount(() => {
  if (timer) clearTimeout(timer)
})
</script>

<template>
  <AsyncState :loading="run.loading.value && !run.data.value" :error="run.error.value" @retry="load">
    <template v-if="summary && run.data.value">
      <PageHeader :title="`${RUN_PAGE.title} ${formatDateTime(summary.startedAt)}`">
        <UiBadge :label="RUN_STATE_LABEL[summary.state] ?? summary.state" data-test="state" />
        <RouterLink :to="{ name: 'source', params: { id: run.data.value.sourceId } }" class="text-accent-ink underline">{{ RUN_PAGE.back }}</RouterLink>
        <CancelRunButton :run-id="summary.id" :state="summary.state" @cancelled="load" />
      </PageHeader>
      <UiCard v-if="progress" class="mb-6" data-test="progress">
        <h2 class="mb-2 font-medium">{{ RUN_PROGRESS.title }}</h2>
        <p class="tabular" aria-live="polite">{{ progress }}</p>
        <p v-if="summary.progress?.limitHit" class="mt-2 text-warn">{{ RUN_PROGRESS.limitHit }}</p>
        <p v-if="summary.progress?.seedsFailed" class="mt-2 text-warn">{{ RUN_PROGRESS.seedsFailed(summary.progress.seedsFailed, summary.progress.seeds ?? 0) }}</p>
        <p class="mt-2 text-small text-fg-2">{{ RUN_PROGRESS.refreshing }}</p>
      </UiCard>
      <div class="mb-6 grid grid-cols-5 gap-3 max-md:grid-cols-2" data-test="counts">
        <MetricTile :label="RUN_PAGE.added" :value="formatNumber(summary.added)" />
        <MetricTile :label="RUN_PAGE.updated" :value="formatNumber(summary.updated)" />
        <MetricTile :label="RUN_PAGE.unchanged" :value="formatNumber(summary.unchanged)" />
        <MetricTile :label="RUN_PAGE.deleted" :value="formatNumber(summary.deleted)" />
        <MetricTile :label="RUN_PAGE.failed" :value="formatNumber(summary.failed)" />
      </div>
      <DeletionDecision
        v-if="summary.state === 'awaiting-deletion-confirmation'" :run-id="summary.id" :proposed-count="summary.proposedDeletions" class="mb-6"
        @decided="load"
      />
      <p v-if="run.data.value?.report?.malwareScan === 'off'" class="mb-6 text-warning" role="note" data-test="not-scanned">{{ RUN_PAGE.notScanned }}</p>
      <UiCard v-if="completeness || verdictReasons.length" class="mb-6" data-test="report">
        <p v-if="completeness?.complete">{{ RUN_PAGE.complete }}</p>
        <template v-else-if="completeness">
          <p>{{ RUN_PAGE.incomplete }}</p>
          <ul class="ml-5 list-disc">
            <li v-for="r in completeness.reasons" :key="r">{{ jobMessage(r) }}</li>
          </ul>
        </template>
        <p v-if="completeness" class="mt-2 text-small text-fg-2">{{ RUN_PAGE.seeds(completeness.seedsResolved, completeness.seeds) }}</p>
        <p v-for="r in verdictReasons" :key="r" class="mt-2 text-warn">{{ jobMessage(r) }}</p>
      </UiCard>
      <UiCard v-if="render" class="mb-6" data-test="render">
        <h2 class="mb-2 font-medium">{{ RUN_PAGE.render }}</h2>
        <dl class="m-0">
          <DetailRow :label="RUN_PAGE.rendered"><span class="tabular">{{ formatNumber(render.rendered) }}</span></DetailRow>
          <DetailRow :label="RUN_PAGE.renderedEmpty"><span class="tabular">{{ formatNumber(render.empty) }}</span></DetailRow>
          <DetailRow :label="RUN_PAGE.renderFailures"><span class="tabular">{{ formatNumber(render.failures) }}</span></DetailRow>
          <DetailRow :label="RUN_PAGE.keptFromLastGood"><span class="tabular">{{ formatNumber(render.keptFromLastGood) }}</span></DetailRow>
        </dl>
      </UiCard>
      <UiCard v-if="politeness.length" class="mb-6" data-test="politeness">
        <h2 class="mb-2 font-medium">{{ RUN_PAGE.politeness }}</h2>
        <ul class="grid gap-1">
          <li v-for="p in politeness" :key="p.host"><span class="font-mono">{{ p.host }}</span>: {{ p.text }}</li>
        </ul>
      </UiCard>

      <h2 class="mb-3 text-title-3">{{ RUN_PAGE.items }}</h2>
      <CheckField v-model="onlyFailed" :label="RUN_PAGE.onlyFailed" class="mb-4" />
      <AsyncState :loading="items.loading.value && items.data.value.length === 0" :error="items.error.value" :empty="items.data.value.length === 0" :empty-text="RUN_PAGE.noItems" @retry="loadItems()">
        <DataTable :columns="columns" :rows="items.data.value" :row-key="(i) => String(i.id)" :caption="RUN_PAGE.items">
          <template #cell-identity="{ row }"><span class="break-all font-mono text-small">{{ row.identity }}</span></template>
          <template #cell-outcome="{ row }">{{ RUN_OUTCOME_LABEL[row.outcome] ?? row.outcome }}</template>
          <template #cell-reason="{ row }"><span v-if="row.reason">{{ jobMessage(row.reason) }}</span></template>
        </DataTable>
        <UiButton v-if="next" class="mt-4" :loading="items.loading.value" @click="loadItems(true)">{{ RUN_PAGE.more }}</UiButton>
      </AsyncState>
    </template>
  </AsyncState>
</template>
