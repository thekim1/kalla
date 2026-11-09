<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Collection, PiiReport, RunSummary, Source } from '@/api/types'
import CancelRunButton from '@/components/runs/CancelRunButton.vue'
import SourceEditDrawer from '@/components/sources/SourceEditDrawer.vue'
import HeldDocuments from '@/components/sources/HeldDocuments.vue'
import PipelineCard from '@/components/sources/PipelineCard.vue'
import SourceFiles from '@/components/sources/SourceFiles.vue'
import SourceQuarantine from '@/components/sources/SourceQuarantine.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
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
import { LEVEL_LABEL, RUN_STATE_LABEL, SOURCE, SOURCE_EDIT, SOURCE_PAGE, SOURCE_TYPE_LABEL } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Källa (admin design §5.5.3, §5.4 step 6; #278 slice 2): the source's settings with Ändra (impact confirmation, V25),
// Hämta nu, Avbryt körning on an active run (#488 part 3), its runs (each opens the run page with the crawl report) and the personal-data report (§4.5 pii-report).
const props = defineProps<{ id: string }>()
const auth = useAuthStore()
const ui = useUiStore()
const router = useRouter()
const source = useLoadable<Source | null>(null)
const runs = useLoadable<RunSummary[]>([])
const pii = ref<PiiReport | null>(null)
const next = ref<string | null>(null)
const syncing = ref(false)
const syncError = ref<unknown>(null)
const editing = ref(false)
const collection = ref<Collection | null>(null)
// The report's numbers and type names (#217): the types whose rule fired and the non-zero counts; never values.
const piiType = (type: string) => SOURCE_PAGE.piiTypes[type] ?? type
const piiTypeList = (types: string[]) => types.map(piiType).join(', ')
const piiCountList = (counts: Record<string, number>) =>
  Object.entries(counts).map(([type, count]) => SOURCE_PAGE.piiCount(piiType(type), formatNumber(count))).join(', ')
const canEdit = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor']))
// The run Cancel run stops (#488 part 3): the newest one still running or waiting for the deletion decision.
const activeRun = computed(() => runs.data.value.find((r) => r.state === 'running' || r.state === 'awaiting-deletion-confirmation'))

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

function stateTone(state: string): 'ok' | 'warn' | 'danger' | 'neutral' {
  return state === 'succeeded' ? 'ok' : state === 'failed' ? 'danger' : state === 'running' ? 'neutral' : 'warn'
}

async function loadRuns(append = false): Promise<void> {
  const previous = append ? runs.data.value : []
  await runs.run(async () => {
    const page = await api.sources.runs(props.id, append ? next.value ?? undefined : undefined)
    next.value = page.next
    return [...previous, ...page.items]
  })
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

function openRun(run: RunSummary): void {
  void router.push({ name: 'run', params: { id: run.id } })
}

onMounted(load)
</script>

<template>
  <AsyncState :loading="source.loading.value && !source.data.value" :error="source.error.value" @retry="load">
    <template v-if="source.data.value">
      <PageHeader :title="source.data.value.name">
        <UiBadge :label="SOURCE_TYPE_LABEL[source.data.value.type]" />
        <UiBadge :label="LEVEL_LABEL[source.data.value.classification]" />
        <UiButton v-if="canEdit" variant="primary" icon="sync" :loading="syncing" data-test="sync" @click="syncNow">{{ SOURCE_PAGE.syncNow }}</UiButton>
        <CancelRunButton v-if="activeRun" :run-id="activeRun.id" :state="activeRun.state" @cancelled="loadRuns()" />
      </PageHeader>
      <InlineError v-if="syncError" :message="problemMessage(syncError)" class="mb-4" />

      <UiCard v-if="source.data.value.type !== 'upload'" :title="SOURCE_PAGE.settings" class="mb-6">
        <dl class="m-0">
          <DetailRow :label="source.data.value.type === 'crawl' ? SOURCE_PAGE.startAddresses : SOURCE_PAGE.addresses">
            <span class="font-mono text-small" data-test="addresses">{{ addresses.join(', ') }}</span>
          </DetailRow>
          <DetailRow :label="SOURCE.fetchMode">
            <span data-test="fetch-mode">{{ SOURCE.fetchModes[source.data.value.settings.fetchMode === 'browser' ? 'browser' : 'http'] }}</span>
          </DetailRow>
          <DetailRow v-if="source.data.value.type === 'crawl'" :label="SOURCE_PAGE.maxPages">
            <span class="tabular">{{ formatNumber(Number(source.data.value.settings.maxPages ?? 10000)) }}</span>
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
      <SourceFiles v-if="source.data.value.type === 'upload'" :source="source.data.value" :can-edit="canEdit" class="mb-6" @committed="loadRuns()" />
      <PipelineCard :source="source.data.value" class="mb-6" />

      <h2 class="mb-3 text-title-3">{{ SOURCE_PAGE.runs }}</h2>
      <AsyncState :loading="runs.loading.value && runs.data.value.length === 0" :error="runs.error.value" :empty="runs.data.value.length === 0" :empty-text="SOURCE_PAGE.noRuns" @retry="loadRuns()">
        <DataTable :columns="runColumns" :rows="runs.data.value" :row-key="(r) => r.id" :caption="SOURCE_PAGE.runs" clickable @row-click="openRun">
          <template #cell-started="{ row }"><span class="tabular">{{ formatDateTime(row.startedAt) }}</span></template>
          <template #cell-state="{ row }"><UiBadge :tone="stateTone(row.state)" :label="RUN_STATE_LABEL[row.state] ?? row.state" /></template>
          <template #cell-counts="{ row }">
            <span class="tabular text-small">{{ SOURCE_PAGE.runCounts(formatNumber(row.added), formatNumber(row.updated), formatNumber(row.deleted)) }}<span
              v-if="row.failed" class="text-danger"
            >, {{ SOURCE_PAGE.runFailed(formatNumber(row.failed)) }}</span></span>
          </template>
        </DataTable>
        <UiButton v-if="next" class="mt-4" :loading="runs.loading.value" @click="loadRuns(true)">{{ SOURCE_PAGE.more }}</UiButton>
      </AsyncState>

      <h2 class="mb-3 mt-8 text-title-3">{{ SOURCE_PAGE.pii }}</h2>
      <p v-if="pii && !pii.applies" class="text-fg-2">{{ SOURCE_PAGE.piiNotApplies }}</p>
      <p v-else-if="pii && pii.documents.length === 0" class="text-fg-2">{{ SOURCE_PAGE.piiNone }}</p>
      <ul v-else-if="pii" class="grid gap-2" data-test="pii">
        <li v-for="d in pii.documents" :key="d.documentId" class="flex flex-wrap items-center gap-2">
          <UiBadge :tone="d.state === 'held' ? 'warn' : 'neutral'" :label="d.state === 'held' ? SOURCE_PAGE.piiHeld : SOURCE_PAGE.piiLive" />
          <span>{{ d.title ?? d.displayName ?? d.url }}</span>
          <span v-if="d.typesFired.length" class="text-small text-warn" data-test="pii-fired">{{ SOURCE_PAGE.piiFired(piiTypeList(d.typesFired)) }}</span>
          <span v-if="Object.keys(d.counts).length" class="tabular text-small text-fg-2" :aria-label="SOURCE_PAGE.piiCounts" data-test="pii-counts">{{ piiCountList(d.counts) }}</span>
        </li>
      </ul>

      <HeldDocuments :source-id="source.data.value.id" class="mt-8" />
      <SourceQuarantine :source-id="source.data.value.id" class="mt-8" />
    </template>
  </AsyncState>
</template>
