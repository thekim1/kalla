<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { Collection, DocumentSummary, SourceSummary } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import CheckField from '@/components/ui/CheckField.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime } from '@/utils/format'
import { COLLECTION, COLLECTIONS, LEVEL_LABEL, SOURCE_TYPE_LABEL } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Kunskapsbank (admin design §5.5.2): Översikt (state, used by), Källor, Dokument with the personal-data filter
// (§4.5 documents). Testfrågor och kvalitet, Sökinställningar and Delning are item 12.
type Tab = 'overview' | 'sources' | 'documents'
const props = defineProps<{ id: string }>()
const auth = useAuthStore()
const ui = useUiStore()
const collection = useLoadable<Collection | null>(null)
const sources = useLoadable<SourceSummary[]>([])
const documents = useLoadable<DocumentSummary[]>([])
const next = ref<string | null>(null)
const onlyPii = ref(false)
const tab = ref<Tab>('overview')
const publishing = ref(false)
const publishError = ref<unknown>(null)
const usedBy = computed(() =>
  (collection.data.value?.usedBy ?? []).map((u) => (u.published ? u.name : `${u.name} (${COLLECTION.draftOnly})`)).join(', '),
)
const canPublish = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin']))

const tabs = computed(() => [
  { value: 'overview' as const, label: COLLECTION.tabs.overview },
  { value: 'sources' as const, label: `${COLLECTION.tabs.sources} (${sources.data.value.length})` },
  { value: 'documents' as const, label: COLLECTION.tabs.documents },
])
const sourceColumns: Column[] = [
  { key: 'name', label: COLLECTION.sourceName },
  { key: 'type', label: COLLECTION.sourceType },
  { key: 'state', label: COLLECTION.sourceState },
]
const documentColumns: Column[] = [
  { key: 'title', label: COLLECTION.documentTitle },
  { key: 'fetched', label: COLLECTION.documentFetched },
  { key: 'state', label: COLLECTION.documentState },
]

async function loadDocuments(append = false): Promise<void> {
  const after = append ? next.value ?? undefined : undefined
  const previous = append ? documents.data.value : []
  await documents.run(async () => {
    const page = await api.documents.list({ collectionId: props.id, pii: onlyPii.value ? 'flagged' : undefined, after, limit: 50 })
    next.value = page.next
    return [...previous, ...page.items]
  })
}

async function load(): Promise<void> {
  await collection.run(() => api.collections.get(props.id))
  await sources.run(() => api.collections.sources(props.id))
}

async function publish(): Promise<void> {
  if (!collection.data.value) return
  publishing.value = true
  publishError.value = null
  try {
    collection.data.value = await api.collections.publish(props.id, collection.data.value.version)
    ui.notify(COLLECTION.published(collection.data.value.name))
  } catch (e) {
    publishError.value = e
  } finally {
    publishing.value = false
  }
}

watch(tab, (t) => {
  if (t === 'documents' && !documents.loaded.value) void loadDocuments()
})
watch(onlyPii, () => void loadDocuments())
onMounted(load)
</script>

<template>
  <AsyncState :loading="collection.loading.value && !collection.data.value" :error="collection.error.value" @retry="load">
    <template v-if="collection.data.value">
      <PageHeader :title="collection.data.value.name">
        <UiBadge :label="LEVEL_LABEL[collection.data.value.classification]" />
        <UiBadge
          :tone="collection.data.value.publishedAt ? 'ok' : 'neutral'"
          :label="collection.data.value.publishedAt ? COLLECTIONS.published : COLLECTIONS.notPublished"
          data-test="state"
        />
        <UiButton v-if="canPublish && !collection.data.value.publishedAt" variant="primary" :loading="publishing" data-test="publish" @click="publish">
          {{ COLLECTION.publish }}
        </UiButton>
      </PageHeader>
      <InlineError v-if="publishError" :message="problemMessage(publishError)" class="mb-4" />
      <UiTabs id="collection" v-model="tab" :tabs="tabs" :label="COLLECTION.tabsLabel" class="mb-6" />

      <div v-if="tab === 'overview'" id="collection-panel-overview" role="tabpanel" aria-labelledby="collection-tab-overview">
        <UiCard :title="COLLECTIONS.state">
          <p v-if="!collection.data.value.publishedAt" class="mb-2">{{ COLLECTIONS.notPublishedHint }}</p>
          <DetailRow :label="COLLECTION.usedBy">
            <span v-if="collection.data.value.usedBy.length === 0" class="text-fg-3">{{ COLLECTION.notUsed }}</span>
            <span v-else data-test="used-by">
              {{ usedBy }}
            </span>
          </DetailRow>
        </UiCard>
      </div>

      <div v-else-if="tab === 'sources'" id="collection-panel-sources" role="tabpanel" aria-labelledby="collection-tab-sources">
        <AsyncState :loading="sources.loading.value" :error="sources.error.value" :empty="sources.data.value.length === 0" :empty-text="COLLECTION.noSources" @retry="load">
          <DataTable :columns="sourceColumns" :rows="sources.data.value" :row-key="(s) => s.id" :caption="COLLECTION.sources">
            <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
            <template #cell-type="{ row }">{{ SOURCE_TYPE_LABEL[row.type as SourceSummary['type']] }}</template>
            <template #cell-state="{ row }">{{ COLLECTION.sourceStates[row.state] ?? row.state }}</template>
          </DataTable>
        </AsyncState>
      </div>

      <div v-else id="collection-panel-documents" role="tabpanel" aria-labelledby="collection-tab-documents">
        <CheckField v-model="onlyPii" :label="COLLECTION.onlyPii" class="mb-4" />
        <AsyncState
          :loading="documents.loading.value && documents.data.value.length === 0" :error="documents.error.value"
          :empty="documents.data.value.length === 0" :empty-text="COLLECTION.noDocuments" @retry="loadDocuments()"
        >
          <DataTable :columns="documentColumns" :rows="documents.data.value" :row-key="(d) => d.id" :caption="COLLECTION.documents">
            <template #cell-title="{ row }">
              <span class="font-medium">{{ row.title ?? row.displayName ?? row.url }}</span>
              <span v-if="row.url" class="block text-small text-fg-3">{{ row.url }}</span>
            </template>
            <template #cell-fetched="{ row }"><span class="tabular text-fg-2">{{ formatDateTime(row.fetchedAt) }}</span></template>
            <template #cell-state="{ row }">
              <span class="flex flex-wrap gap-1">
                <UiBadge :tone="row.live ? 'ok' : 'neutral'" :label="row.live ? COLLECTION.live : COLLECTION.notLive" />
                <UiBadge v-if="row.piiFlagged && !row.piiCleared" tone="warn" :label="COLLECTION.pii" data-test="pii" />
                <UiBadge v-if="row.piiCleared" tone="accent" :label="COLLECTION.piiCleared" />
                <UiBadge v-if="row.held" tone="warn" :label="COLLECTION.held" />
              </span>
            </template>
          </DataTable>
          <UiButton v-if="next" class="mt-4" :loading="documents.loading.value" @click="loadDocuments(true)">{{ COLLECTION.more }}</UiButton>
        </AsyncState>
      </div>
    </template>
  </AsyncState>
</template>
