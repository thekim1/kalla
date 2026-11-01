<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Collection, CollectionSummary, Department } from '@/api/types'
import CollectionCreateDrawer from '@/components/collections/CollectionCreateDrawer.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { COLLECTIONS, LEVEL_LABEL, PAGE } from '@/utils/labels'

// Kunskapsbanker (admin design §5.3, §5.4 steps 1–2): the collections of the user's departments; editors create one.
const auth = useAuthStore()
const ui = useUiStore()
const router = useRouter()
const collections = useLoadable<CollectionSummary[]>([])
const departments = ref<Department[]>([])
const q = ref('')
const creating = ref(false)
const canEdit = auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor'])

const columns: Column[] = [
  { key: 'name', label: COLLECTIONS.name },
  { key: 'department', label: COLLECTIONS.department },
  { key: 'level', label: COLLECTIONS.level },
  { key: 'state', label: COLLECTIONS.state },
]

async function load(): Promise<void> {
  await collections.run(async () => (await api.collections.list({ q: q.value.trim() || undefined, pageSize: 200 })).items)
}

function departmentName(id: string): string {
  return departments.value.find((d) => d.id === id)?.name ?? ''
}

function open(row: CollectionSummary): void {
  void router.push({ name: 'collection', params: { id: row.id } })
}

function created(collection: Collection): void {
  ui.notify(COLLECTIONS.created(collection.name))
  void router.push({ name: 'collection', params: { id: collection.id } })
}

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
  <AsyncState :loading="collections.loading.value" :error="collections.error.value" :empty="collections.data.value.length === 0" :empty-text="COLLECTIONS.empty" @retry="load">
    <DataTable :columns="columns" :rows="collections.data.value" :row-key="(c) => c.id" :caption="COLLECTIONS.list" clickable @row-click="open">
      <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
      <template #cell-department="{ row }">{{ departmentName(row.departmentId) }}</template>
      <template #cell-level="{ row }">{{ LEVEL_LABEL[row.classification as CollectionSummary['classification']] }}</template>
      <template #cell-state="{ row }">
        <UiBadge :tone="row.publishedAt ? 'ok' : 'neutral'" :label="row.publishedAt ? COLLECTIONS.published : COLLECTIONS.notPublished" />
      </template>
    </DataTable>
  </AsyncState>
  <CollectionCreateDrawer v-model:open="creating" :departments="departments" @created="created" />
</template>
