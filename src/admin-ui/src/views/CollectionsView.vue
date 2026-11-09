<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Collection, CollectionSummary, CollectionView, Department } from '@/api/types'
import CollectionCreateDrawer from '@/components/collections/CollectionCreateDrawer.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatNumber } from '@/utils/format'
import { COLLECTIONS, LEVEL_LABEL, PAGE, SHARING_LABEL } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Knowledge bases (admin design §5.2, §5.3, §5.4 steps 1–2): the tabs Ours, Shared with us and All (`view=`, §4.5);
// editors create one. A shared row is another department's: owner, level, sharing, documents and topics only.
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
const canEdit = auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor'])

const columns: Column[] = [
  { key: 'name', label: COLLECTIONS.name },
  { key: 'department', label: COLLECTIONS.department },
  { key: 'level', label: COLLECTIONS.level },
  { key: 'sharing', label: COLLECTIONS.sharing },
  { key: 'documents', label: COLLECTIONS.documents, align: 'right' },
  { key: 'state', label: COLLECTIONS.state },
]

async function load(): Promise<void> {
  await collections.run(async () => (await api.collections.list({ q: q.value.trim() || undefined, pageSize: 200, view: view.value })).items)
}

function open(row: CollectionSummary): void {
  void router.push({ name: 'collection', params: { id: row.id } })
}

/** A knowledge base of files opens on its Files tab (§5.5.7); without its source (it failed) the tab offers Add files. */
function created(collection: Collection, content: 'files' | 'web', filesError: unknown): void {
  ui.notify(filesError ? COLLECTIONS.createdNoFiles(collection.name, problemMessage(filesError)) : COLLECTIONS.created(collection.name))
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
  <form class="mb-4 max-w-sm" role="search" @submit.prevent="load">
    <TextField id="collections-search" v-model="q" :label="COLLECTIONS.search" type="search" />
  </form>
  <UiTabs id="collections" v-model="view" :tabs="views" :label="COLLECTIONS.viewsLabel" class="mb-4" />
  <div :id="`collections-panel-${view}`" role="tabpanel" :aria-labelledby="`collections-tab-${view}`">
  <AsyncState :loading="collections.loading.value" :error="collections.error.value" :empty="collections.data.value.length === 0" :empty-text="emptyText" @retry="load">
    <DataTable :columns="columns" :rows="collections.data.value" :row-key="(c) => c.id" :caption="COLLECTIONS.list" clickable @row-click="open">
      <template #cell-name="{ row }">
        <span class="font-medium">{{ row.name }}</span>
        <UiBadge v-if="row.shared" tone="accent" :label="COLLECTIONS.shared" class="ml-2" data-test="shared" />
        <span v-if="row.topics.length" class="block text-small text-fg-3">{{ COLLECTIONS.topics }}: {{ row.topics.join(', ') }}</span>
      </template>
      <template #cell-department="{ row }">{{ row.departmentName }}</template>
      <template #cell-level="{ row }">{{ LEVEL_LABEL[row.classification as CollectionSummary['classification']] }}</template>
      <template #cell-sharing="{ row }">{{ SHARING_LABEL[row.sharedWith as CollectionSummary['sharedWith']] }}</template>
      <template #cell-documents="{ row }"><span class="tabular">{{ formatNumber(row.documentCount) }}</span></template>
      <template #cell-state="{ row }">
        <UiBadge :tone="row.publishedAt ? 'ok' : 'neutral'" :label="row.publishedAt ? COLLECTIONS.published : COLLECTIONS.notPublished" />
      </template>
    </DataTable>
  </AsyncState>
  </div>
  <CollectionCreateDrawer v-model:open="creating" :departments="departments" @created="created" />
</template>
