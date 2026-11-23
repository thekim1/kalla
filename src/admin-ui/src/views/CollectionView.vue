<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import { useKeysetList } from '@/composables/useKeysetList'
import { useTabQuery } from '@/composables/useTabQuery'
import type { Collection, CostQuery, CollectionRead, CollectionSetup, DocumentSummary, EvaluationOverview, Source, SourceListItem, SourceSummary } from '@/api/types'
import CollectionSharing from '@/components/collections/CollectionSharing.vue'
import DocumentFeedbackCard from '@/components/collections/DocumentFeedbackCard.vue'
import CollectionTopics from '@/components/collections/CollectionTopics.vue'
import SetupChecklist from '@/components/collections/SetupChecklist.vue'
import EvaluationTab from '@/components/evaluation/EvaluationTab.vue'
import RetrievalSettingsForm from '@/components/evaluation/RetrievalSettingsForm.vue'
import FilesPanel from '@/components/files/FilesPanel.vue'
import { createUploadSource } from '@/components/files/files'
import SourceDrawer from '@/components/sources/SourceDrawer.vue'
import CollectionEditDrawer from '@/components/collections/CollectionEditDrawer.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SelectField from '@/components/ui/SelectField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import UiDialog from '@/components/ui/UiDialog.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { useLoadable } from '@/stores/loadable'
import CostCard from '@/components/costs/CostCard.vue'
import KeyBudgetStatus from '@/components/gateway/KeyBudgetStatus.vue'
import KeyUnavailableBadge from '@/components/gateway/KeyUnavailableBadge.vue'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatDecimal, formatNumber } from '@/utils/format'
import { COLLECTION, COLLECTIONS, DELETE, EDIT_DRAWER, EVALUATION, LEVEL_LABEL, MOVE_DEPARTMENT, RETRIEVAL, RUN_STATE_LABEL, SOURCE, SOURCE_TYPE_LABEL } from '@/utils/labels'
import MoveDepartmentDrawer from '@/components/organisation/MoveDepartmentDrawer.vue'
import { problemMessage } from '@/utils/problem'
import PiiActions from '@/components/documents/PiiActions.vue'

// Kunskapsbank (admin design §5.5.2): Översikt (state, contact, used by), Filer (§5.5.7: the Files card of an upload
// source, with a picker when there are several; shown when there is one, and Add files on Källor creates it), Källor,
// Dokument with the personal-data filter (§4.5 documents), Topics, Testfrågor och kvalitet, Sökinställningar (§7.1) and
// Sharing (§3.2). A department the knowledge base is shared with opens it read-only: Overview (owner, contact, level,
// document count) and Topics.
type Tab = 'overview' | 'files' | 'sources' | 'documents' | 'topics' | 'evaluation' | 'retrieval' | 'sharing'
const props = defineProps<{ id: string }>()
const auth = useAuthStore()
const ui = useUiStore()
const router = useRouter()
const setup = ref<CollectionSetup | null>(null)
const read = useLoadable<CollectionRead | null>(null)
/** The owner's detail; null for a shared knowledge base. */
const collection = { data: computed<Collection | null>(() => (read.data.value && !read.data.value.shared ? read.data.value : null)) }
const shared = computed(() => (read.data.value?.shared ? read.data.value : null))
const sources = useLoadable<SourceListItem[]>([])
const evaluation = ref<EvaluationOverview | null>(null)
// `?pii=flagged` (Home's personal-data item, B-59) opens the Documents tab with the flagged filter on.
const onlyPii = ref(router.currentRoute?.value?.query.pii === 'flagged')
// The Documents tab; toggling the filter while a page loads keeps only the newest answer (#279 review).
const documents = useKeysetList((after?: string) =>
  api.documents.list({ collectionId: props.id, pii: onlyPii.value ? 'flagged' : undefined, after, limit: 50 }))
const TABS: [Tab, ...Tab[]] = ['overview', 'files', 'sources', 'documents', 'topics', 'evaluation', 'retrieval', 'sharing']
/** The tab is kept in `?tab=` (the setup checklist links to them; a reload keeps it). */
const tab = useTabQuery(TABS)
const publishing = ref(false)
const publishError = ref<unknown>(null)
/** The assistants that use it, each with its label; the Overview links each to its page (§5.5.2). */
const users = computed(() =>
  (collection.data.value?.usedBy ?? []).map((u) => {
    // Another department's assistant is named with its department (a shared knowledge base, §3.2).
    const name = u.departmentId === collection.data.value?.departmentId ? u.name : `${u.name}, ${u.departmentName}`
    // An archived one stays listed (it still blocks deletion and can be restored) but says so (#374).
    if (u.state === 'archived') return { id: u.id, label: `${name} (${COLLECTION.archived})` }
    return { id: u.id, label: u.published ? name : `${name} (${COLLECTION.draftOnly})` }
  }),
)
const usedBy = computed(() => users.value.map((u) => u.label).join(', '))
const lastRun = computed(() =>
  sources.data.value.map((s) => s.lastRun).filter((r) => r !== null).sort((a, b) => b.startedAt.localeCompare(a.startedAt))[0] ?? null,
)
const quality = computed(() => {
  const finished = (evaluation.value?.latestRuns ?? []).filter((r) => r.finishedAt && !r.candidate && r.recallAtK !== null)
  return finished.find((r) => r.variant === 'hybrid') ?? finished[0] ?? null
})
/** The Overview's Cost card (admin design §8.6). */
const collectionCosts = (query: CostQuery) => api.costs.collection(collection.data.value!.id, query)
const canPublish = computed(() => auth.can('manage'))
const canEdit = computed(() => auth.can('edit'))
const adding = ref(false)
const uploadSources = computed(() => sources.data.value.filter((s) => s.type === 'upload'))
const fileSourceId = ref('')
const fileSource = useLoadable<Source | null>(null)
const addingFiles = ref(false)
const addFilesError = ref<unknown>(null)
/** `?tab=files` on a knowledge base without an upload source (its creation failed, or none was made): Add files says so. */
const noFilesSource = ref(false)
// Edit (§5.5.2, #446): the own fields, the level included.
const editing = ref(false)
// Change department (#811): platform admins only, on the owner's view.
const canMove = computed(() => auth.can('admin'))
const moving = ref(false)
// Delete (Decisions "Deleting knowledge bases, sources and assistants"; admin §5.5): Manage, the name typed; while an
// assistant links it or it has a source, the dialog names them and has no delete button (the API refuses with
// collection_in_use).
const canDelete = computed(() => auth.can('manage'))
// B-63: a flagged document's highlights and Clear are Manage's (V26; the API checks the source's department).
const canManage = computed(() => auth.can('manage') && !shared.value)
/** A document whose personal-data actions apply: flagged and not cleared, or held again after a clearance (processing §9.5). */
const needsPiiDecision = (row: DocumentSummary) => row.piiHeld || (row.piiFlagged && !row.piiCleared)
const deleting = ref(false)
const inUse = computed(() => users.value.length > 0 || sources.data.value.length > 0)
async function deleteCollection(): Promise<void> {
  const deleted = collection.data.value!
  await api.collections.remove(deleted.id, deleted.version)
  ui.notify(DELETE.knowledgeBaseDeleted(deleted.name))
  void router.push({ name: 'collections' })
}
const proposal = ref<{ coverageMaxDistance?: number; coverageMinKeywordScore?: number } | null>(null)

const tabs = computed(() => shared.value ? [
  { value: 'overview' as const, label: COLLECTION.tabs.overview },
  { value: 'topics' as const, label: COLLECTION.tabs.topics },
] : [
  { value: 'overview' as const, label: COLLECTION.tabs.overview },
  ...(uploadSources.value.length ? [{ value: 'files' as const, label: COLLECTION.tabs.files }] : []),
  // The count only once loaded (#279 review: no "(0)" while loading).
  { value: 'sources' as const, label: sources.loaded.value ? `${COLLECTION.tabs.sources} (${sources.data.value.length})` : COLLECTION.tabs.sources },
  { value: 'documents' as const, label: COLLECTION.tabs.documents },
  { value: 'topics' as const, label: COLLECTION.tabs.topics },
  { value: 'evaluation' as const, label: EVALUATION.tab },
  { value: 'retrieval' as const, label: RETRIEVAL.tab },
  { value: 'sharing' as const, label: COLLECTION.tabs.sharing },
])
const sourceColumns: Column[] = [
  { key: 'name', label: COLLECTION.sourceName },
  { key: 'type', label: COLLECTION.sourceType },
  { key: 'lastRun', label: COLLECTION.sourceLastRun },
  { key: 'documents', label: COLLECTION.sourceDocuments, align: 'right' },
  { key: 'state', label: COLLECTION.sourceState },
]
const documentColumns: Column[] = [
  { key: 'title', label: COLLECTION.documentTitle },
  { key: 'fetched', label: COLLECTION.documentFetched },
  { key: 'state', label: COLLECTION.documentState },
]

async function load(): Promise<void> {
  const loaded = await read.run(() => api.collections.get(props.id))
  if (!loaded || loaded.shared) {
    if (loaded?.shared && !tabs.value.some((t) => t.value === tab.value)) tab.value = 'overview'
    return
  }
  await sources.run(() => api.collections.sources(props.id))
  pickFileSource()
  evaluation.value = await api.collections.evaluation(props.id).catch(() => null)
  // Kom igång (§5.4): computed by the API; hidden once done or when it can't be read.
  setup.value = await api.collections.setup(props.id).catch(() => null)
  // Opened on Documents (a link or a reload): the tab watch below fires only on a change.
  if (tab.value === 'documents' && !documents.loaded.value) void documents.reload()
}

/** The upload source the Files tab shows: the one picked while it still exists, else the first; none sends Files to Sources. */
function pickFileSource(): void {
  if (!uploadSources.value.some((s) => s.id === fileSourceId.value)) fileSourceId.value = uploadSources.value[0]?.id ?? ''
  if (tab.value === 'files' && !fileSourceId.value && sources.loaded.value) {
    tab.value = 'sources'
    noFilesSource.value = true
  }
}

async function loadFileSource(): Promise<void> {
  const id = fileSourceId.value
  if (!id || fileSource.data.value?.id === id) return
  await fileSource.run(() => api.sources.get(id))
  // Another source picked meanwhile: its answer is the one to show.
  if (fileSourceId.value !== id && !fileSource.error.value) await loadFileSource()
}

/** Add files (§5.5.7): the upload source a knowledge base of files gets, then its Files tab. */
async function addFiles(): Promise<void> {
  if (!collection.data.value) return
  addingFiles.value = true
  addFilesError.value = null
  try {
    const source = await createUploadSource(collection.data.value)
    await sources.run(() => api.collections.sources(props.id))
    fileSourceId.value = source.id
    noFilesSource.value = false
    tab.value = 'files'
  } catch (e) {
    addFilesError.value = e
  } finally {
    addingFiles.value = false
  }
}

function edited(saved: Collection): void {
  read.data.value = saved
  ui.notify(EDIT_DRAWER.saved)
}

function moved(saved: Collection, department: string): void {
  read.data.value = saved
  ui.notify(MOVE_DEPARTMENT.moved(department))
}

async function publish(): Promise<void> {
  if (!collection.data.value) return
  publishing.value = true
  publishError.value = null
  try {
    const saved = await api.collections.publish(props.id, collection.data.value.version)
    read.data.value = saved
    ui.notify(COLLECTION.published(saved.name))
  } catch (e) {
    publishError.value = e
  } finally {
    publishing.value = false
  }
}

function sourceSaved(source: Source, syncing: boolean): void {
  ui.notify(syncing ? SOURCE.saved(source.name) : SOURCE.savedNoSync(source.name))
  // §5.4 step 6: from saving to the source page, where the run shows.
  void router.push({ name: 'source', params: { id: source.id } })
}

/** The report's coverage-threshold proposal goes to Sökinställningar, filled in for the editor to save (§7.1). */
function useProposal(values: { coverageMaxDistance?: number; coverageMinKeywordScore?: number }): void {
  proposal.value = { ...values }
  tab.value = 'retrieval'
}

function retrievalSaved(saved: Collection): void {
  read.data.value = { ...saved }
  proposal.value = null
}

function sharingSaved(saved: Collection): void {
  read.data.value = { ...saved }
}

const sourceTo = (source: SourceSummary) => ({ name: 'source', params: { id: source.id } })

watch(tab, (t) => {
  if (t === 'documents' && !documents.loaded.value) void documents.reload()
})
watch(onlyPii, () => void documents.reload())
watch([tab, fileSourceId], ([t]) => {
  if (t !== 'files') return
  if (sources.loaded.value && !fileSourceId.value) pickFileSource()
  else void loadFileSource()
})
onMounted(load)
</script>

<template>
  <AsyncState :loading="read.loading.value && !read.data.value" :error="read.error.value" @retry="load">
    <template v-if="shared">
      <PageHeader :title="shared.name" :description="shared.departmentName">
        <UiBadge :label="LEVEL_LABEL[shared.classification]" />
        <UiBadge tone="accent" :label="COLLECTIONS.shared" data-test="shared" />
      </PageHeader>
      <UiTabs id="collection" v-model="tab" :tabs="tabs" :label="COLLECTION.tabsLabel" class="mb-6" />
      <div v-if="tab === 'topics'" id="collection-panel-topics" role="tabpanel" aria-labelledby="collection-tab-topics">
        <CollectionTopics :collection-id="shared.id" />
      </div>
      <div v-else id="collection-panel-overview" role="tabpanel" aria-labelledby="collection-tab-overview" class="grid gap-6">
        <UiCard :title="COLLECTIONS.state">
          <p class="mb-2" data-test="shared-with-you">{{ COLLECTION.sharedWithYou(shared.departmentName) }}</p>
          <p v-if="!shared.publishedAt" class="mb-2">{{ COLLECTIONS.notPublishedHint }}</p>
          <dl>
            <DetailRow :label="COLLECTION.owner">{{ shared.departmentName }}</DetailRow>
            <DetailRow :label="COLLECTION.contact">
              <span v-if="shared.contactName || shared.contactEmail" data-test="contact">{{ [shared.contactName, shared.contactEmail].filter(Boolean).join(', ') }}</span>
              <span v-else class="text-fg-3">{{ COLLECTION.noContact }}</span>
            </DetailRow>
            <DetailRow :label="COLLECTION.level">{{ LEVEL_LABEL[shared.classification] }}</DetailRow>
            <DetailRow :label="COLLECTION.documents"><span class="tabular" data-test="document-count">{{ formatNumber(shared.documentCount) }}</span></DetailRow>
          </dl>
        </UiCard>
      </div>
    </template>
    <template v-else-if="collection.data.value">
      <PageHeader :title="collection.data.value.name">
        <UiBadge :label="LEVEL_LABEL[collection.data.value.classification]" />
        <UiBadge
          :tone="collection.data.value.publishedAt ? 'ok' : 'neutral'"
          :label="collection.data.value.publishedAt ? COLLECTIONS.published : COLLECTIONS.notPublished"
          data-test="state"
        />
        <UiButton v-if="canEdit" icon="edit" data-test="edit" @click="editing = true">{{ EDIT_DRAWER.edit }}</UiButton>
        <UiButton v-if="canMove && !shared" data-test="move-department" @click="moving = true">{{ MOVE_DEPARTMENT.action }}</UiButton>
        <UiButton v-if="canDelete && !shared" variant="danger" icon="delete" data-test="delete" @click="deleting = true">{{ DELETE.action }}</UiButton>
        <UiButton v-if="canPublish && !collection.data.value.publishedAt" variant="primary" :loading="publishing" data-test="publish" @click="publish">
          {{ COLLECTION.publish }}
        </UiButton>
      </PageHeader>
      <InlineError v-if="publishError" :message="problemMessage(publishError)" class="mb-4" />
      <UiTabs id="collection" v-model="tab" :tabs="tabs" :label="COLLECTION.tabsLabel" class="mb-6" />

      <div v-if="tab === 'overview'" id="collection-panel-overview" role="tabpanel" aria-labelledby="collection-tab-overview" class="grid gap-6">
        <SetupChecklist v-if="setup && !setup.done" :setup="setup" />
        <UiCard :title="COLLECTIONS.state">
          <KeyUnavailableBadge class="mb-2" :gateway-key-id="collection.data.value.gatewayKeyId" :department-id="collection.data.value.departmentId" />
          <KeyBudgetStatus class="mb-3" :gateway-key-id="collection.data.value.gatewayKeyId" />
          <p v-if="!collection.data.value.publishedAt" class="mb-2">{{ COLLECTIONS.notPublishedHint }}</p>
          <dl>
            <DetailRow :label="COLLECTION.contact">
              <span v-if="collection.data.value.contactName || collection.data.value.contactEmail" data-test="contact">
                {{ [collection.data.value.contactName, collection.data.value.contactEmail].filter(Boolean).join(', ') }}
              </span>
              <span v-else class="flex flex-wrap items-center gap-3">
                <span class="text-fg-3">{{ COLLECTION.noContact }}</span>
                <UiButton v-if="canEdit" size="sm" icon="add" data-test="add-contact" @click="editing = true">{{ COLLECTION.addContact }}</UiButton>
              </span>
            </DetailRow>
            <DetailRow :label="COLLECTION.usedBy">
              <span v-if="users.length === 0" class="text-fg-3">{{ COLLECTION.notUsed }}</span>
              <span v-else data-test="used-by">
                <template v-for="(u, i) in users" :key="u.id">
                  <template v-if="i > 0">, </template>
                  <RouterLink :to="{ name: 'assistant', params: { id: u.id } }" class="link inline-flex min-h-6 items-center" :data-test="`used-by-${u.id}`">{{ u.label }}</RouterLink>
                </template>
              </span>
            </DetailRow>
            <!-- The live documents, as on the knowledge base list (#374). -->
            <DetailRow :label="COLLECTION.documents"><span class="tabular" data-test="document-count">{{ formatNumber(collection.data.value.documentCount) }}</span></DetailRow>
            <template v-if="sources.loaded.value">
              <DetailRow :label="COLLECTION.lastFetch">
                <span v-if="lastRun" data-test="last-fetch">
                  <span class="tabular">{{ formatDateTime(lastRun.finishedAt ?? lastRun.startedAt) }}</span> · {{ RUN_STATE_LABEL[lastRun.state] ?? lastRun.state }}
                </span>
                <span v-else class="text-fg-3">{{ COLLECTION.notFetched }}</span>
              </DetailRow>
            </template>
            <DetailRow :label="COLLECTION.quality(quality?.k ?? 10)">
              <span v-if="quality" data-test="quality">
                <span class="tabular">{{ formatDecimal(quality.recallAtK!, 2, 2) }}</span>
                <span class="text-fg-2"> · {{ formatDateTime(quality.finishedAt) }}</span>
              </span>
              <span v-else class="text-fg-3">{{ COLLECTION.notMeasured }}</span>
            </DetailRow>
          </dl>
        </UiCard>
        <!-- Admin §8.6: indexing and evaluation of the last 30 days; this panel is the owning department's (a shared one sees no cost). -->
        <CostCard
          kind="collection" :source="collectionCosts" :gateway-key-id="collection.data.value.gatewayKeyId" :department-id="collection.data.value.departmentId"
          :costs-link="canPublish ? { name: 'costs', query: { departmentId: collection.data.value.departmentId } } : null"
        />
        <!-- Admin §8.8: the ratings of the answers that cited its documents, over every assistant; the owning department's only. -->
        <DocumentFeedbackCard :collection-id="collection.data.value.id" />
      </div>

      <div v-else-if="tab === 'files'" id="collection-panel-files" role="tabpanel" aria-labelledby="collection-tab-files" class="grid gap-4">
        <div v-if="uploadSources.length > 1" class="max-w-sm">
          <SelectField
            id="files-source" v-model="fileSourceId" :label="COLLECTION.filesSource" :options="uploadSources.map((s) => ({ value: s.id, label: s.name }))"
          />
        </div>
        <AsyncState
          :loading="sources.loading.value || (fileSource.loading.value && fileSource.data.value?.id !== fileSourceId)" :error="fileSource.error.value"
          @retry="loadFileSource"
        >
          <FilesPanel
            v-if="fileSource.data.value && fileSource.data.value.id === fileSourceId" :key="fileSource.data.value.id" :source="fileSource.data.value"
            :can-edit="canEdit" topics-link :used-by="usedBy" @topics="tab = 'topics'"
          />
        </AsyncState>
      </div>

      <div v-else-if="tab === 'sources'" id="collection-panel-sources" role="tabpanel" aria-labelledby="collection-tab-sources">
        <div v-if="canEdit" class="mb-4 flex flex-wrap items-center justify-end gap-2">
          <p v-if="noFilesSource && sources.loaded.value && uploadSources.length === 0" class="mr-auto text-small text-fg-2" data-test="no-files-source">
            {{ COLLECTION.noFilesSource }}
          </p>
          <UiButton
            v-if="sources.loaded.value && uploadSources.length === 0" icon="upload_file" :loading="addingFiles" data-test="add-files" @click="addFiles"
          >
            {{ COLLECTION.addFiles }}
          </UiButton>
          <UiButton variant="primary" icon="add" data-test="add-source" @click="adding = true">{{ SOURCE.add }}</UiButton>
        </div>
        <InlineError v-if="addFilesError" :message="problemMessage(addFilesError)" class="mb-4" />
        <AsyncState :loading="sources.loading.value" :error="sources.error.value" :empty="sources.data.value.length === 0" :empty-text="COLLECTION.noSources" @retry="load">
          <DataTable :columns="sourceColumns" :rows="sources.data.value" :row-key="(s) => s.id" :caption="COLLECTION.sources" :row-to="sourceTo">
            <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
            <template #cell-type="{ row }">{{ SOURCE_TYPE_LABEL[row.type as SourceSummary['type']] }}</template>
            <template #cell-lastRun="{ row }">
              <template v-if="row.lastRun">
                <span class="tabular">{{ formatDateTime(row.lastRun.finishedAt ?? row.lastRun.startedAt) }}</span> · {{ RUN_STATE_LABEL[row.lastRun.state] ?? row.lastRun.state }}
              </template>
              <span v-else class="text-fg-3">{{ COLLECTION.sourceNoRun }}</span>
            </template>
            <template #cell-documents="{ row }"><span class="tabular">{{ formatNumber(row.documents) }}</span></template>
            <template #cell-state="{ row }">{{ COLLECTION.sourceStates[row.state] ?? row.state }}</template>
          </DataTable>
        </AsyncState>
      </div>

      <div v-else-if="tab === 'topics'" id="collection-panel-topics" role="tabpanel" aria-labelledby="collection-tab-topics">
        <CollectionTopics :collection-id="collection.data.value.id" :sources="sources.data.value" />
      </div>

      <div v-else-if="tab === 'sharing'" id="collection-panel-sharing" role="tabpanel" aria-labelledby="collection-tab-sharing">
        <CollectionSharing :collection="collection.data.value" :can-manage="canPublish" @saved="sharingSaved" />
      </div>

      <div v-else-if="tab === 'evaluation'" id="collection-panel-evaluation" role="tabpanel" aria-labelledby="collection-tab-evaluation">
        <EvaluationTab :collection-id="collection.data.value.id" :can-edit="canEdit" @use-proposal="useProposal" />
      </div>

      <div v-else-if="tab === 'retrieval'" id="collection-panel-retrieval" role="tabpanel" aria-labelledby="collection-tab-retrieval">
        <RetrievalSettingsForm :collection="collection.data.value" :can-edit="canEdit" :proposal="proposal" @saved="retrievalSaved" @tried="tab = 'evaluation'" />
      </div>

      <div v-else id="collection-panel-documents" role="tabpanel" aria-labelledby="collection-tab-documents">
        <CheckField v-model="onlyPii" :label="COLLECTION.onlyPii" class="mb-4" />
        <AsyncState
          :loading="documents.loading.value && documents.items.value.length === 0" :error="documents.error.value"
          :empty="documents.items.value.length === 0" :empty-text="COLLECTION.noDocuments" @retry="documents.reload()"
        >
          <DataTable :columns="documentColumns" :rows="documents.items.value" :row-key="(d) => d.id" :caption="COLLECTION.documents">
            <template #cell-title="{ row }">
              <span class="font-medium" lang="sv">{{ row.title ?? row.displayName ?? row.url }}</span>
              <span v-if="row.url" class="block text-small text-fg-3">{{ row.url }}</span>
            </template>
            <template #cell-fetched="{ row }"><span class="tabular text-fg-2">{{ formatDateTime(row.fetchedAt) }}</span></template>
            <template #cell-state="{ row }">
              <span class="flex flex-wrap gap-1">
                <UiBadge :tone="row.live ? 'ok' : 'neutral'" :label="row.live ? COLLECTION.live : COLLECTION.notLive" />
                <UiBadge v-if="needsPiiDecision(row)" tone="warn" :label="COLLECTION.pii" data-test="pii" />
                <UiBadge v-if="row.piiCleared && !row.piiHeld" tone="accent" :label="COLLECTION.piiCleared" />
                <UiBadge v-if="row.held" tone="warn" :label="COLLECTION.held" />
              </span>
              <PiiActions
                v-if="canManage && needsPiiDecision(row)" class="mt-1" :document-id="row.id" :title="row.title ?? row.displayName ?? row.url ?? row.id"
                @cleared="documents.reload()"
              />
            </template>
          </DataTable>
          <UiButton v-if="documents.next.value" class="mt-4" :loading="documents.loading.value" @click="documents.more()">{{ COLLECTION.more }}</UiButton>
        </AsyncState>
      </div>
      <SourceDrawer v-model:open="adding" :collection="collection.data.value" @saved="sourceSaved" />
      <CollectionEditDrawer v-if="canEdit" v-model:open="editing" :collection="collection.data.value" @saved="edited" />
      <MoveDepartmentDrawer
        v-if="canMove && !shared" :id="collection.data.value.id" v-model:open="moving" kind="collection"
        :department-id="collection.data.value.departmentId" :version="collection.data.value.version" @moved-collection="moved"
      />
      <template v-if="canDelete && !shared">
        <UiDialog v-if="inUse" v-model:open="deleting" :title="DELETE.inUseTitle" icon="block" tone="warn">
          <template #description>
            <p v-if="users.length > 0" data-test="in-use-assistants">{{ DELETE.inUseAssistants(usedBy) }}</p>
            <p v-if="sources.data.value.length > 0" data-test="in-use-sources">{{ DELETE.inUseSources(formatNumber(sources.data.value.length)) }}</p>
          </template>
          <template #actions><UiButton @click="deleting = false">{{ DELETE.close }}</UiButton></template>
        </UiDialog>
        <ConfirmDialog
          v-else v-model:open="deleting" :title="DELETE.knowledgeBaseTitle" :confirm-label="DELETE.action" danger
          :consequence="DELETE.knowledgeBaseConsequence(formatNumber(collection.data.value.documentCount))"
          :confirm-name="collection.data.value.name" :confirm-name-label="DELETE.typeName(collection.data.value.name)" :action="deleteCollection"
        />
      </template>
    </template>
  </AsyncState>
</template>
