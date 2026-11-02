<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { Run, RunItem } from '@/api/types'
import DeletionDecision from '@/components/sources/DeletionDecision.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import MetricTile from '@/components/ui/MetricTile.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { pollDelay } from '@/composables/useJobPoll'
import { useLoadable } from '@/stores/loadable'
import { formatDateTime, formatNumber } from '@/utils/format'
import { RUN_OUTCOME_LABEL, RUN_PAGE, RUN_STATE_LABEL } from '@/utils/labels'
import { jobMessage } from '@/utils/messages'

// Körning (admin design §5.5.3, §5.4 step 6; #278 slice 2): live counters while it runs (polled as §4.7), the crawl
// report (completeness and its reasons, the verdict), the deletion decision when the guard held deletions back, and the
// run's documents with their outcome and reason in Swedish.
const props = defineProps<{ id: string }>()
const run = useLoadable<Run | null>(null)
const items = useLoadable<RunItem[]>([])
const next = ref<number | null>(null)
const onlyFailed = ref(false)
let timer: ReturnType<typeof setTimeout> | null = null
const started = Date.now()

const columns: Column[] = [
  { key: 'identity', label: RUN_PAGE.document },
  { key: 'outcome', label: RUN_PAGE.outcome },
  { key: 'reason', label: RUN_PAGE.reason },
]
const summary = computed(() => run.data.value?.run)
const completeness = computed(() => run.data.value?.report?.completeness)
const verdictReasons = computed(() =>
  [run.data.value?.report?.verdict?.runFailure, run.data.value?.report?.verdict?.runDegradation].filter((r): r is string => !!r),
)

async function loadItems(append = false): Promise<void> {
  const previous = append ? items.data.value : []
  await items.run(async () => {
    const page = await api.runs.items(props.id, { outcome: onlyFailed.value ? 'failed' : undefined, after: append ? next.value ?? undefined : undefined, limit: 50 })
    next.value = page.next
    return [...previous, ...page.items]
  })
}

async function load(): Promise<void> {
  await run.run(() => api.runs.get(props.id))
  if (summary.value?.state === 'running') timer = setTimeout(() => void load(), pollDelay(Date.now() - started))
  else void loadItems()
}

watch(onlyFailed, () => void loadItems())
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
      </PageHeader>
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
