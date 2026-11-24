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
import { formatDate, formatNumber, quietZero } from '@/utils/format'
import { ASSISTANTS, LEVEL_LABEL, PAGE } from '@/utils/labels'

// Assistants (admin design §5.3, §5.4 step 9, §5.5.4 "The assistant list"; #278 slice 3): a row is a link to the assistant.
const auth = useAuthStore()
const ui = useUiStore()
const router = useRouter()
const assistants = useLoadable<AssistantSummary[]>([])
const departments = ref<Department[]>([])
const creating = ref(false)
const canEdit = auth.can('edit')
const columns: Column[] = [
  { key: 'name', label: ASSISTANTS.name },
  { key: 'level', label: ASSISTANTS.level },
  { key: 'collections', label: ASSISTANTS.collections },
  { key: 'model', label: ASSISTANTS.model },
  { key: 'published', label: ASSISTANTS.published },
  { key: 'questions7d', label: ASSISTANTS.questions7d, align: 'right' },
]
const rowTo = (a: AssistantSummary) => ({ name: 'assistant', params: { id: a.id } })

async function load(): Promise<void> {
  // Archived assistants last, marked Archived (admin design §5.5.4 badges); otherwise the API's order.
  const archived = (a: AssistantSummary) => (a.state === 'archived' ? 1 : 0)
  await assistants.run(async () => (await api.assistants.list({ pageSize: 200 })).items.sort((a, b) => archived(a) - archived(b)))
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
      :columns="columns" :rows="assistants.data.value" :row-key="(a) => a.id" :caption="ASSISTANTS.list" :row-to="rowTo"
    >
      <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span><span class="block font-mono text-small text-fg-3">{{ row.slug }}</span></template>
      <template #cell-level="{ row }">{{ LEVEL_LABEL[row.classification as AssistantSummary['classification']] }}</template>
      <template #cell-collections="{ row }">
        <span v-if="row.collections.length" :data-test="`collections-${row.id}`">{{ row.collections.map((c: AssistantSummary['collections'][number]) => c.name).join(', ') }}</span>
        <span v-else class="text-fg-3">{{ ASSISTANTS.noCollections }}</span>
      </template>
      <template #cell-model="{ row }">
        <span v-if="row.model">{{ row.model }}</span>
        <span v-else class="text-fg-3">{{ ASSISTANTS.noModel }}</span>
      </template>
      <template #cell-published="{ row }">
        <UiBadge v-if="row.state === 'archived'" :label="ASSISTANTS.archived" :data-test="`archived-${row.id}`" />
        <span v-else-if="row.publishedVersion !== null" :data-test="`published-${row.id}`">
          <UiBadge tone="ok" :label="ASSISTANTS.publishedVersion(row.publishedVersion)" />
          <span class="ml-2 tabular text-small text-fg-2">{{ formatDate(row.publishedAt) }}</span>
          <span v-if="row.hasDraft" class="text-small text-fg-2" :data-test="`draft-changes-${row.id}`"> · {{ ASSISTANTS.draftHasChanges }}</span>
        </span>
        <UiBadge v-else :label="ASSISTANTS.draftOnly" />
        <UiBadge
          v-if="row.generalKnowledge && row.generalKnowledge !== 'off'" :label="ASSISTANTS.generalKnowledge" class="ml-2"
          :data-test="`general-knowledge-${row.id}`"
        />
      </template>
      <template #cell-questions7d="{ row }"><span class="tabular" :class="quietZero(row.questions7d)">{{ formatNumber(row.questions7d) }}</span></template>
    </DataTable>
  </AsyncState>
  <AssistantCreateDrawer v-model:open="creating" :departments="departments" @created="created" />
</template>
