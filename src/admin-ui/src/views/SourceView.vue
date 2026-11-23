<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Collection, PiiReport, RunSummary, Source } from '@/api/types'
import CancelRunButton from '@/components/runs/CancelRunButton.vue'
import ContinueRunButton from '@/components/runs/ContinueRunButton.vue'
import FilesPanel from '@/components/files/FilesPanel.vue'
import SourceEditDrawer from '@/components/sources/SourceEditDrawer.vue'
import { describeSchedule } from '@/components/sources/schedule'
import HeldDocuments from '@/components/sources/HeldDocuments.vue'
import PipelineCard from '@/components/sources/PipelineCard.vue'
import SourceQuarantine from '@/components/sources/SourceQuarantine.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatNumber } from '@/utils/format'
import { DELETE, LEVEL_LABEL, RUN_STATE_LABEL, SOURCE, SOURCE_EDIT, SOURCE_PAGE, SOURCE_TYPE_LABEL } from '@/utils/labels'
import { pollDelay } from '@/composables/useJobPoll'
import { useKeysetList } from '@/composables/useKeysetList'
import { runProgressText, runStateTone } from '@/utils/runs'
import { problemMessage } from '@/utils/problem'

// Källa (admin design §5.5.3, §5.4 step 6; #278 slice 2): the source's settings with Ändra (impact confirmation, V25),
// Hämta nu, Avbryt körning on an active run (#488 part 3), the schedule in force with the next scheduled fetch (engine
// design §9.6), its runs (each opens the run page with the crawl report) and the personal-data report (§4.5 pii-report).
const props = defineProps<{ id: string }>()
const auth = useAuthStore()
const ui = useUiStore()
const source = useLoadable<Source | null>(null)
const runs = useKeysetList((after?: string) => api.sources.runs(props.id, after))
const pii = ref<PiiReport | null>(null)
const syncing = ref(false)
const syncError = ref<unknown>(null)
const editing = ref(false)
const router = useRouter()
// Delete (Decisions "Deleting knowledge bases, sources and assistants"; admin §5.5): Manage, at any time, the name typed;
// the dialog says how many documents leave the search (the collection's source list carries the count).
const canDelete = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin']))
const deleting = ref(false)
const documentCount = ref<number | null>(null)
async function openDelete(): Promise<void> {
  const current = source.data.value!
  documentCount.value = await api.collections.sources(current.collectionId)
    .then((list) => list.find((s) => s.id === current.id)?.documents ?? null)
    .catch(() => null)
  deleting.value = true
}
async function deleteSource(): Promise<void> {
  const deleted = source.data.value!
  await api.sources.remove(deleted.id, deleted.version)
  ui.notify(DELETE.sourceDeleted(deleted.name))
  void router.push({ name: 'collection', params: { id: deleted.collectionId } })
}
const collection = ref<Collection | null>(null)
// The report's numbers and type names (#217): the types whose rule fired and the non-zero counts; never values.
const piiType = (type: string) => SOURCE_PAGE.piiTypes[type] ?? type
const piiTypeList = (types: string[]) => types.map(piiType).join(', ')
const piiCountList = (counts: Record<string, number>) =>
  Object.entries(counts).map(([type, count]) => SOURCE_PAGE.piiCount(piiType(type), formatNumber(count))).join(', ')
type PiiDocument = PiiReport['documents'][number]
const piiFlagged = (d: PiiDocument) => d.typesFired.length > 0
/** The report as a table, flagged documents first (admin design §5.5.3); otherwise in the report's order. */
const piiRows = computed(() => {
  const documents = pii.value?.documents ?? []
  return [...documents.filter(piiFlagged), ...documents.filter((d) => !piiFlagged(d))]
})
const piiFlaggedCount = computed(() => piiRows.value.filter(piiFlagged).length)
const piiColumns: Column[] = [
  { key: 'document', label: SOURCE_PAGE.piiDocument },
  { key: 'fired', label: SOURCE_PAGE.piiFlaggedFor },
  { key: 'counts', label: SOURCE_PAGE.piiCounts },
  { key: 'state', label: SOURCE_PAGE.state },
]
const canEdit = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor']))
// The run Cancel run stops (#488 part 3): the newest one still running or waiting for the deletion decision.
const activeRun = computed(() => runs.items.value.find((r) => r.state === 'running' || r.state === 'awaiting-deletion-confirmation'))
const activeProgress = computed(() => runProgressText(activeRun.value))
// #803: a running run is polled (admin design §4.7) and its row replaced in place, so paging through older runs is kept;
// when it stops, the list is loaded again.
let runTimer: ReturnType<typeof setTimeout> | null = null
let runPollStarted = 0
function stopRunPoll(): void {
  if (runTimer) clearTimeout(runTimer)
  runTimer = null
}
async function pollRun(id: string): Promise<void> {
  runTimer = null
  const fresh = await api.runs.get(id).then((r) => r.run).catch(() => null)
  if (!fresh) return
  runs.items.value = runs.items.value.map((r) => (r.id === fresh.id ? fresh : r))
  if (fresh.state !== 'running') void loadRuns()
}
watch(
  () => (activeRun.value?.state === 'running' ? activeRun.value : null),
  (running) => {
    stopRunPoll()
    if (!running) return
    if (!runPollStarted) runPollStarted = Date.now()
    runTimer = setTimeout(() => void pollRun(running.id), pollDelay(Date.now() - runPollStarted))
  },
)
onBeforeUnmount(stopRunPoll)

const runColumns: Column[] = [
  { key: 'started', label: SOURCE_PAGE.started },
  { key: 'state', label: SOURCE_PAGE.state },
  { key: 'counts', label: SOURCE_PAGE.counts },
]
const addresses = computed(() => {
  const settings = source.data.value?.settings ?? {}
  const list = (settings.startUrls ?? settings.urls ?? []) as string[]
  return list
})
const sitemaps = computed(() => ((source.data.value?.settings.sitemaps ?? []) as string[]))
const scheduleText = computed(() => {
  const current = source.data.value
  const text = describeSchedule(current?.scheduleInForce ?? null)
  if (!current || !text) return null
  return current.schedule ? text : SOURCE_PAGE.scheduleDefault(text)
})
const nextRunText = computed(() => {
  const current = source.data.value
  if (!current) return null
  if (current.state === 'paused') return SOURCE_PAGE.nextRunPaused
  if (!current.nextRunAt) return null
  return new Date(current.nextRunAt).getTime() <= Date.now() ? SOURCE_PAGE.nextRunDue : formatDateTime(current.nextRunAt)
})
const demoted = computed(() =>
  ((source.data.value?.settings.demote ?? []) as { pattern: string; factor?: number }[]).map((d) => `${d.pattern} ×${String(d.factor ?? 0.5).replace('.', ',')}`),
)

async function edit(): Promise<void> {
  // A source's knowledge base is the caller's own, so this is the owner's detail.
  collection.value ??= await api.collections.get(source.data.value!.collectionId).then((c) => (c.shared ? null : c)).catch(() => null)
  editing.value = true
}

function saved(updated: Source): void {
  source.data.value = updated
  ui.notify(SOURCE_EDIT.saved)
}

async function loadRuns(): Promise<void> {
  await runs.reload()
  await loadContinuable()
}

// Continue beside Sync now (#804): the latest run of a crawl source, when it ended with pages left or failed.
const continuable = ref<string | null>(null)
async function loadContinuable(): Promise<void> {
  const latest = runs.items.value[0]
  continuable.value = null
  if (source.data.value?.type !== 'crawl' || !latest || latest.state === 'running' || latest.state === 'awaiting-deletion-confirmation') return
  continuable.value = (await api.runs.get(latest.id).catch(() => null))?.canContinue ? latest.id : null
}

async function load(): Promise<void> {
  await source.run(() => api.sources.get(props.id))
  await loadRuns()
  pii.value = await api.sources.piiReport(props.id).catch(() => null)
}

async function syncNow(): Promise<void> {
  syncing.value = true
  syncError.value = null
  try {
    const started = await api.sources.sync(props.id)
    ui.notify(started.alreadyRunning ? SOURCE_PAGE.alreadyRunning : SOURCE_PAGE.syncStarted)
    await loadRuns()
  } catch (e) {
    syncError.value = e
  } finally {
    syncing.value = false
  }
}

const runTo = (run: RunSummary) => ({ name: 'run', params: { id: run.id } })

onMounted(load)
</script>

<template>
  <AsyncState :loading="source.loading.value && !source.data.value" :error="source.error.value" @retry="load">
    <template v-if="source.data.value">
      <PageHeader :title="source.data.value.name">
        <UiBadge :label="SOURCE_TYPE_LABEL[source.data.value.type]" />
        <UiBadge :label="LEVEL_LABEL[source.data.value.classification]" />
        <UiButton v-if="canEdit" variant="primary" icon="sync" :loading="syncing" data-test="sync" @click="syncNow">{{ SOURCE_PAGE.syncNow }}</UiButton>
        <ContinueRunButton v-if="continuable && !activeRun" :run-id="continuable" can-continue @started="loadRuns()" />
        <CancelRunButton v-if="activeRun" :run-id="activeRun.id" :state="activeRun.state" @cancelled="loadRuns()" />
        <UiButton v-if="canDelete" variant="danger" icon="delete" data-test="delete" @click="openDelete">{{ DELETE.action }}</UiButton>
      </PageHeader>
      <InlineError v-if="syncError" :message="problemMessage(syncError)" class="mb-4" />
      <p v-if="activeProgress && activeRun" class="mb-4 tabular" aria-live="polite" data-test="run-progress">
        <RouterLink :to="runTo(activeRun)" class="link">{{ activeProgress }}</RouterLink>
      </p>

      <UiCard v-if="source.data.value.type !== 'upload'" :title="SOURCE_PAGE.settings" class="mb-6">
        <dl class="m-0">
          <DetailRow :label="source.data.value.type === 'crawl' ? SOURCE_PAGE.startAddresses : SOURCE_PAGE.addresses">
            <span class="font-mono text-small" data-test="addresses">{{ addresses.join(', ') }}</span>
          </DetailRow>
          <DetailRow v-if="sitemaps.length" :label="SOURCE_PAGE.sitemaps">
            <span class="font-mono text-small" data-test="sitemaps">{{ sitemaps.join(', ') }}</span>
          </DetailRow>
          <DetailRow :label="SOURCE.fetchMode">
            <span data-test="fetch-mode">{{ SOURCE.fetchModes[source.data.value.settings.fetchMode === 'browser' ? 'browser' : 'http'] }}</span>
          </DetailRow>
          <DetailRow v-if="source.data.value.type === 'crawl'" :label="SOURCE_PAGE.maxPages">
            <span class="tabular">{{ formatNumber(Number(source.data.value.settings.maxPages ?? 10000)) }}</span>
          </DetailRow>
          <DetailRow v-if="scheduleText" :label="SOURCE_PAGE.schedule">
            <span data-test="schedule">{{ scheduleText }}</span>
          </DetailRow>
          <DetailRow v-if="nextRunText" :label="SOURCE_PAGE.nextRun">
            <span class="tabular" data-test="next-run">{{ nextRunText }}</span>
          </DetailRow>
          <DetailRow v-if="demoted.length" :label="SOURCE_EDIT.demoted">
            <span class="font-mono text-small" data-test="demoted">{{ demoted.join(', ') }}</span>
          </DetailRow>
        </dl>
        <div v-if="canEdit" class="mt-4">
          <UiButton icon="edit" data-test="edit-source" @click="edit">{{ SOURCE_EDIT.edit }}</UiButton>
        </div>
      </UiCard>
      <SourceEditDrawer v-model:open="editing" :source="source.data.value" :collection="collection" @saved="saved" />
      <FilesPanel v-if="source.data.value.type === 'upload'" :source="source.data.value" :can-edit="canEdit" class="mb-6" @committed="loadRuns()" />
      <PipelineCard :source="source.data.value" class="mb-6" />

      <h2 class="mb-3 text-title-3">{{ SOURCE_PAGE.runs }}</h2>
      <AsyncState :loading="runs.loading.value && runs.items.value.length === 0" :error="runs.error.value" :empty="runs.items.value.length === 0" :empty-text="SOURCE_PAGE.noRuns" @retry="loadRuns()">
        <DataTable :columns="runColumns" :rows="runs.items.value" :row-key="(r) => r.id" :caption="SOURCE_PAGE.runs" :row-to="runTo">
          <template #cell-started="{ row }"><span class="tabular">{{ formatDateTime(row.startedAt) }}</span></template>
          <template #cell-state="{ row }"><UiBadge :tone="runStateTone(row.state)" :label="RUN_STATE_LABEL[row.state] ?? row.state" /></template>
          <template #cell-counts="{ row }">
            <span class="tabular text-small">{{ SOURCE_PAGE.runCounts(formatNumber(row.added), formatNumber(row.updated), formatNumber(row.deleted)) }}<span
              v-if="row.failed" class="text-danger"
            >, <RouterLink :to="{ name: 'run', params: { id: row.id } }" class="link text-danger" data-test="run-failed" @click.stop>{{
              SOURCE_PAGE.runFailed(formatNumber(row.failed)) }}</RouterLink></span></span>
          </template>
        </DataTable>
        <UiButton v-if="runs.next.value" class="mt-4" :loading="runs.loading.value" @click="runs.more()">{{ SOURCE_PAGE.more }}</UiButton>
      </AsyncState>

      <!-- Folded by default, with the counts in the summary; flagged documents first (admin design §5.5.3). There is no
           document page to link to, so the names are plain text. -->
      <details v-if="pii && pii.applies && pii.documents.length" class="mt-8" data-test="pii">
        <summary class="cursor-pointer"><h2 class="inline text-title-3" data-test="pii-summary">{{ SOURCE_PAGE.piiSummary(piiRows.length, piiFlaggedCount) }}</h2></summary>
        <DataTable :columns="piiColumns" :rows="piiRows" :row-key="(d) => d.documentId" :caption="SOURCE_PAGE.pii" class="mt-3">
          <template #cell-document="{ row }"><span data-test="pii-document">{{ row.title ?? row.displayName ?? row.url }}</span></template>
          <template #cell-fired="{ row }">
            <span v-if="row.typesFired.length" class="text-small text-warn" data-test="pii-fired">{{ piiTypeList(row.typesFired) }}</span>
          </template>
          <template #cell-counts="{ row }">
            <span v-if="Object.keys(row.counts).length" class="tabular text-small text-fg-2" data-test="pii-counts">{{ piiCountList(row.counts) }}</span>
          </template>
          <template #cell-state="{ row }">
            <UiBadge :tone="row.state === 'held' ? 'warn' : 'neutral'" :label="row.state === 'held' ? SOURCE_PAGE.piiHeld : SOURCE_PAGE.piiLive" />
          </template>
        </DataTable>
      </details>
      <template v-else>
        <h2 class="mb-3 mt-8 text-title-3">{{ SOURCE_PAGE.pii }}</h2>
        <p v-if="pii && !pii.applies" class="text-fg-2">{{ SOURCE_PAGE.piiNotApplies }}</p>
        <p v-else-if="pii" class="text-fg-2">{{ SOURCE_PAGE.piiNone }}</p>
      </template>

      <HeldDocuments :source-id="source.data.value.id" class="mt-8" />
      <SourceQuarantine :source-id="source.data.value.id" class="mt-8" />
      <ConfirmDialog
        v-if="canDelete" v-model:open="deleting" :title="DELETE.sourceTitle" :confirm-label="DELETE.action" danger
        :consequence="DELETE.sourceConsequence(documentCount === null ? '–' : formatNumber(documentCount))"
        :confirm-name="source.data.value.name" :confirm-name-label="DELETE.typeName(source.data.value.name)" :action="deleteSource"
      />
    </template>
  </AsyncState>
</template>
