<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Assistant, AssistantSummary, Department } from '@/api/types'
import AssistantCreateDrawer from '@/components/assistants/AssistantCreateDrawer.vue'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { ASSISTANTS, LEVEL_LABEL, PAGE } from '@/utils/labels'

// Assistenter (admin design §5.3, §5.4 step 9; #278 slice 3).
const auth = useAuthStore()
const ui = useUiStore()
const router = useRouter()
const assistants = useLoadable<AssistantSummary[]>([])
const departments = ref<Department[]>([])
const creating = ref(false)
const canEdit = auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor'])
const columns: Column[] = [
  { key: 'name', label: ASSISTANTS.name },
  { key: 'level', label: ASSISTANTS.level },
  { key: 'state', label: ASSISTANTS.state },
]

async function load(): Promise<void> {
  await assistants.run(async () => (await api.assistants.list({ pageSize: 200 })).items)
}

function created(assistant: Assistant): void {
  ui.notify(ASSISTANTS.created(assistant.name))
  void router.push({ name: 'assistant', params: { id: assistant.id } })
}

onMounted(async () => {
  departments.value = await api.organisation.departments().catch(() => [])
  await load()
})
</script>

<template>
  <PageHeader :title="PAGE.assistants.title" :description="ASSISTANTS.lead">
    <UiButton v-if="canEdit" variant="primary" icon="add" data-test="create" @click="creating = true">{{ ASSISTANTS.create }}</UiButton>
  </PageHeader>
  <AsyncState :loading="assistants.loading.value" :error="assistants.error.value" :empty="assistants.data.value.length === 0" :empty-text="ASSISTANTS.empty" @retry="load">
    <DataTable
      :columns="columns" :rows="assistants.data.value" :row-key="(a) => a.id" :caption="ASSISTANTS.list" clickable
      @row-click="(a: AssistantSummary) => router.push({ name: 'assistant', params: { id: a.id } })"
    >
      <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span><span class="block font-mono text-small text-fg-3">{{ row.slug }}</span></template>
      <template #cell-level="{ row }">{{ LEVEL_LABEL[row.classification as AssistantSummary['classification']] }}</template>
      <template #cell-state="{ row }"><UiBadge :tone="row.published ? 'ok' : 'neutral'" :label="row.published ? ASSISTANTS.published : ASSISTANTS.draftOnly" /></template>
    </DataTable>
  </AsyncState>
  <AssistantCreateDrawer v-model:open="creating" :departments="departments" @created="created" />
</template>
