<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
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
import PageBreadcrumb from '@/components/layout/PageBreadcrumb.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatNumber } from '@/utils/format'
import { DELETE, LEVEL_LABEL, PAGE, PII_ACTIONS, RUN_STATE_LABEL, SOURCE, SOURCE_EDIT, SOURCE_PAGE, SOURCE_TYPE_LABEL } from '@/utils/labels'
import { useKeysetList } from '@/composables/useKeysetList'
import { usePoll } from '@/composables/usePoll'
import { runProgressText, runStateTone } from '@/utils/runs'
import { problemMessage } from '@/utils/problem'
import { isApiError } from '@/api/client'
import PiiActions from '@/components/documents/PiiActions.vue'

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
const canDelete = computed(() => auth.can('manage'))
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
// The breadcrumb (admin design §5.12): the knowledge bases, then the source's knowledge base by name once it has loaded.
const collectionName = ref<string | null>(null)
const crumbs = computed(() => [
  { label: PAGE.collections.title, to: { name: 'collections' } },
  ...(source.data.value && collectionName.value ? [{ label: collectionName.value, to: { name: 'collection', params: { id: source.data.value.collectionId } } }] : []),
])
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
  { key: 'actions', label: PII_ACTIONS.clear, hideLabel: true, align: 'right' },
]
// B-63: a flagged document's highlights and Clear are Manage's (V26; the API checks the source's department).
const canManage = computed(() => auth.can('manage'))
const canEdit = computed(() => auth.can('edit'))
// The run Cancel run stops (#488 part 3): the newest one still running or waiting for the deletion decision.
const activeRun = computed(() => runs.items.value.find((r) => r.state === 'running' || r.state === 'awaiting-deletion-confirmation'))
const activeProgress = computed(() => runProgressText(activeRun.value))
// #803: a running run is polled (admin design §4.7) and its row replaced in place, so paging through older runs is kept;
// when it stops, the list is loaded again. A failed read ends the poll; the row keeps its last state.
const runPoll = usePoll(async (current) => {
  const running = activeRun.value
  if (running?.state !== 'running') return true
  const fresh = (await api.runs.get(running.id)).run
  if (!current()) return true
  runs.items.value = runs.items.value.map((r) => (r.id === fresh.id ? fresh : r))
  if (fresh.state === 'running') return false
  void loadRuns()
  return true
})
// A poll's own patch replaces the row too, so a poll already running is left to go on.
watch(
  () => (activeRun.value?.state === 'running' ? activeRun.value : null),
  (running) => {
    if (!running) runPoll.stop()
    else if (!runPoll.polling.value) runPoll.start(false)
  },
)

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
  const collectionId = source.data.value?.collectionId
  // One read of the knowledge base: its name for the breadcrumb, and (when it is the caller's own) the edit drawer's copy.
  if (collectionId) {
    void api.collections.get(collectionId).then((c) => {
      collectionName.value = c.name
      if (!c.shared) collection.value ??= c
    }).catch(() => undefined)
  }
  await loadRuns()
  pii.value = await api.sources.piiReport(props.id).catch(() => null)
}

// Pause and resume (admin design §5.5.3; B-70): the source's version goes with the call; a version conflict reloads the
// source and says so, so a second try acts on what is saved now.
const paused = computed(() => source.data.value?.state === 'paused')
const pausing = ref(false)
async function togglePause(): Promise<void> {
  const current = source.data.value
  if (!current) return
  pausing.value = true
  syncError.value = null
  try {
    source.data.value = paused.value ? await api.sources.resume(current.id, current.version) : await api.sources.pause(current.id, current.version)
    ui.notify(paused.value ? SOURCE_PAGE.paused : SOURCE_PAGE.resumed)
  } catch (e) {
    if (isApiError(e) && e.status === 409) {
      await source.run(() => api.sources.get(props.id))
      ui.notify(SOURCE_PAGE.pauseConflict)
    } else {
      syncError.value = e
    }
  } finally {
    pausing.value = false
  }
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

async function reloadPii(): Promise<void> {
  pii.value = await api.sources.piiReport(props.id).catch(() => pii.value)
}
</script>

<template>
  <AsyncState :loading="source.loading.value && !source.data.value" :error="source.error.value" @retry="load">
    <template v-if="source.data.value">
      <PageBreadcrumb :items="crumbs" />
      <PageHeader :title="source.data.value.name">
        <UiBadge :label="SOURCE_TYPE_LABEL[source.data.value.type]" />
        <UiBadge :label="LEVEL_LABEL[source.data.value.classification]" />
        <UiBadge v-if="paused" tone="warn" :label="SOURCE_PAGE.pausedBadge" data-test="paused" />
        <UiButton v-if="canEdit" variant="primary" icon="sync" :loading="syncing" data-test="sync" @click="syncNow">{{ SOURCE_PAGE.syncNow }}</UiButton>
        <UiButton
          v-if="canEdit" :icon="paused ? 'play_arrow' : undefined" :loading="pausing" :data-test="paused ? 'resume' : 'pause'" @click="togglePause"
        >
          {{ paused ? SOURCE_PAGE.resume : SOURCE_PAGE.pause }}
        </UiButton>
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

      <!-- Section titles inside their cards (admin design §5.12, Every page). -->
      <UiCard :title="SOURCE_PAGE.runs" class="mb-6" data-test="runs">
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
      </UiCard>

      <!-- Folded by default, with the counts in the summary; flagged documents first (admin design §5.5.3). There is no
           document page to link to, so the names are plain text. -->
      <UiCard v-if="pii && pii.applies && pii.documents.length" as="details" class="mb-6" data-test="pii">
        <summary class="cursor-pointer"><h2 class="inline text-heading" data-test="pii-summary">{{ SOURCE_PAGE.piiSummary(piiRows.length, piiFlaggedCount) }}</h2></summary>
        <DataTable :columns="piiColumns" :rows="piiRows" :row-key="(d) => d.documentId" :caption="SOURCE_PAGE.pii" class="mt-3">
          <template #cell-document="{ row }"><span data-test="pii-document">{{ row.title ?? row.displayName ?? row.url }}</span></template>
          <template #cell-fired="{ row }">
            <span v-if="row.typesFired.length" class="text-small text-warn" data-test="pii-fired">{{ piiTypeList(row.typesFired) }}</span>
          </template>
          <template #cell-counts="{ row }">
            <span v-if="Object.keys(row.counts).length" class="tabular text-small text-fg-2" data-test="pii-counts">{{ piiCountList(row.counts) }}</span>
          </template>
          <template #cell-state="{ row }">
            <span class="flex flex-wrap gap-1">
              <UiBadge :tone="row.state === 'held' ? 'warn' : 'neutral'" :label="row.state === 'held' ? SOURCE_PAGE.piiHeld : SOURCE_PAGE.piiLive" />
              <UiBadge v-if="row.piiClearedAt && row.state !== 'held'" tone="accent" :label="PII_ACTIONS.clearedBadge" data-test="pii-cleared" />
            </span>
          </template>
          <template #cell-actions="{ row }">
            <PiiActions
              v-if="canManage && (row.state === 'held' || (row.typesFired.length && !row.piiClearedAt))" :document-id="row.documentId" :title="row.title ?? row.displayName ?? row.url ?? row.documentId"
              @cleared="reloadPii"
            />
          </template>
        </DataTable>
      </UiCard>
      <UiCard v-else :title="SOURCE_PAGE.pii" class="mb-6">
        <p v-if="pii && !pii.applies" class="text-fg-2">{{ SOURCE_PAGE.piiNotApplies }}</p>
        <p v-else-if="pii" class="text-fg-2">{{ SOURCE_PAGE.piiNone }}</p>
      </UiCard>

      <!-- Kept documents and quarantined files, under a name that says what they are (admin design §5.7); neither is about
           personal data. -->
      <UiCard :title="SOURCE_PAGE.heldAndQuarantined" class="mt-8" data-test="held-and-quarantined">
        <HeldDocuments :source-id="source.data.value.id" />
        <SourceQuarantine :source-id="source.data.value.id" class="mt-6" />
      </UiCard>
      <ConfirmDialog
        v-if="canDelete" v-model:open="deleting" :title="DELETE.sourceTitle" :confirm-label="DELETE.action" danger
        :consequence="DELETE.sourceConsequence(documentCount === null ? '–' : formatNumber(documentCount))"
        :confirm-name="source.data.value.name" :confirm-name-label="DELETE.typeName(source.data.value.name)" :action="deleteSource"
      />
    </template>
  </AsyncState>
</template>
