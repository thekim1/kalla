<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Collection, CollectionSummary, CollectionView, Department } from '@/api/types'
import CollectionCreateDrawer from '@/components/collections/CollectionCreateDrawer.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDate, formatDateTimeToday, formatDecimal, formatNumber, quietZero } from '@/utils/format'
import { COLLECTIONS, LEVEL_LABEL, PAGE, RUN_STATE_LABEL, SHARING_LABEL } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import { runStateTone } from '@/utils/runs'

// Knowledge bases (admin design §5.2, §5.3, §5.4 steps 1–2, §5.5.2): the tabs Ours, Shared with us and All (`view=`, §4.5);
// editors create one. A shared row is another department's: owner, level, sharing, documents and topics only (its Last
// fetch and Quality are empty). A row is a link to the knowledge base.
const auth = useAuthStore()
const ui = useUiStore()
const router = useRouter()
const collections = useLoadable<CollectionSummary[]>([])
const departments = ref<Department[]>([])
const q = ref('')
const view = ref<CollectionView>('mine')
const views = [
  { value: 'mine' as const, label: COLLECTIONS.views.mine },
  { value: 'shared' as const, label: COLLECTIONS.views.shared },
  { value: 'all' as const, label: COLLECTIONS.views.all },
]
const emptyText = computed(() => (view.value === 'shared' ? COLLECTIONS.emptyShared : view.value === 'all' ? COLLECTIONS.emptyAll : COLLECTIONS.empty))
const creating = ref(false)
const canEdit = auth.can('edit')

const columns: Column[] = [
  { key: 'name', label: COLLECTIONS.name },
  { key: 'department', label: COLLECTIONS.department },
  { key: 'level', label: COLLECTIONS.levelColumn },
  { key: 'sharing', label: COLLECTIONS.sharing },
  { key: 'documents', label: COLLECTIONS.documents, align: 'right' },
  { key: 'lastFetch', label: COLLECTIONS.lastFetch },
  { key: 'quality', label: COLLECTIONS.quality },
  { key: 'state', label: COLLECTIONS.state },
]

async function load(): Promise<void> {
  await collections.run(async () => (await api.collections.list({ q: q.value.trim() || undefined, pageSize: 200, view: view.value })).items)
}

const rowTo = (row: CollectionSummary) => ({ name: 'collection', params: { id: row.id } })

/** A knowledge base of files opens on Content's file list (§5.5.7); without its source (it failed) Content offers Add files. */
function created(collection: Collection, content: 'files' | 'web', filesError: unknown): void {
  ui.notify(filesError ? COLLECTIONS.createdNoFiles(collection.name, problemMessage(filesError)) : COLLECTIONS.created(collection.name))
  // `?tab=files` opens Content and asks for the file list: it says so there when the upload source could not be made.
  void router.push({ name: 'collection', params: { id: collection.id }, ...(content === 'files' ? { query: { tab: 'files' } } : {}) })
}

watch(view, () => void load())

onMounted(async () => {
  departments.value = await api.organisation.departments().catch(() => [])
  await load()
})
</script>

<template>
  <PageHeader :title="PAGE.collections.title" :description="COLLECTIONS.lead">
    <UiButton v-if="canEdit" variant="primary" icon="add" data-test="create" @click="creating = true">{{ COLLECTIONS.create }}</UiButton>
  </PageHeader>
  <!-- The search sits on the tabs' row, right-aligned, with a placeholder and no label above (admin design §5.7). -->
  <div class="mb-4 flex flex-wrap items-center justify-between gap-3">
    <UiTabs id="collections" v-model="view" :tabs="views" :label="COLLECTIONS.viewsLabel" />
    <form class="w-full sm:w-72" role="search" @submit.prevent="load">
      <input
        id="collections-search" v-model="q" type="search" class="field-input" :placeholder="COLLECTIONS.searchPlaceholder" :aria-label="COLLECTIONS.search"
        autocomplete="off" data-test="collections-search"
      >
    </form>
  </div>
  <div :id="`collections-panel-${view}`" role="tabpanel" :aria-labelledby="`collections-tab-${view}`">
  <AsyncState :loading="collections.loading.value" :error="collections.error.value" :empty="collections.data.value.length === 0" :empty-text="emptyText" @retry="load">
    <DataTable :columns="columns" :rows="collections.data.value" :row-key="(c) => c.id" :caption="COLLECTIONS.list" :row-to="rowTo">
      <template #cell-name="{ row }">
        <span class="font-medium">{{ row.name }}</span>
        <UiBadge v-if="row.shared" tone="accent" :label="COLLECTIONS.shared" class="ml-2" data-test="shared" />
        <span v-if="row.topics.length" class="block text-small text-fg-3">{{ COLLECTIONS.topics }}: {{ row.topics.join(', ') }}</span>
      </template>
      <template #cell-department="{ row }">{{ row.departmentName }}</template>
      <template #cell-level="{ row }">{{ LEVEL_LABEL[row.classification as CollectionSummary['classification']] }}</template>
      <template #cell-sharing="{ row }">{{ SHARING_LABEL[row.sharedWith as CollectionSummary['sharedWith']] }}</template>
      <template #cell-documents="{ row }"><span class="tabular" :class="quietZero(row.documentCount)">{{ formatNumber(row.documentCount) }}</span></template>
      <template #cell-lastFetch="{ row }">
        <RouterLink v-if="row.lastRun" :to="{ name: 'run', params: { id: row.lastRun.id } }" class="text-fg hover:text-fg hover:underline" :data-test="`last-fetch-${row.id}`">
          <span class="tabular">{{ formatDateTimeToday(row.lastRun.startedAt) }}</span>
          <UiBadge :tone="runStateTone(row.lastRun.state)" :label="RUN_STATE_LABEL[row.lastRun.state] ?? row.lastRun.state" class="ml-2" />
        </RouterLink>
        <span v-else class="text-fg-3">{{ row.shared ? COLLECTIONS.noValue : COLLECTIONS.neverFetched }}</span>
      </template>
      <template #cell-quality="{ row }">
        <span v-if="row.latestEvaluation" :data-test="`quality-${row.id}`">
          <span class="tabular">{{ formatDecimal(row.latestEvaluation.recallAtK, 2, 2) }}</span>
          <span class="text-fg-2"> · {{ formatDate(row.latestEvaluation.finishedAt) }}</span>
        </span>
        <span v-else class="text-fg-3">{{ COLLECTIONS.noValue }}</span>
      </template>
      <template #cell-state="{ row }">
        <UiBadge :tone="row.publishedAt ? 'ok' : 'neutral'" :label="row.publishedAt ? COLLECTIONS.published : COLLECTIONS.notPublished" />
      </template>
    </DataTable>
  </AsyncState>
  </div>
  <CollectionCreateDrawer v-model:open="creating" :departments="departments" @created="created" />
</template>
