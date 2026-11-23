<script setup lang="ts">
import { onMounted } from 'vue'
import { api } from '@/api'
import type { Topic } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import { useLoadable } from '@/stores/loadable'
import { formatNumber, quietZero } from '@/utils/format'
import { TOPICS } from '@/utils/labels'

// Topics (admin design §5.5.2): the tags the sources' path rules put on the chunks, with document counts. The owner also
// sees the rule that sets each one; a department the knowledge base is shared with sees the topics only.
// The owner's view passes the knowledge base's sources: the rules are edited on each source's Processing card (#929).
const props = defineProps<{ collectionId: string; sources?: { id: string; name: string }[] }>()
const topics = useLoadable<Topic[]>([])
const owner = () => topics.data.value.some((t) => t.rules !== null)
const columns = (): Column[] => [
  { key: 'tag', label: TOPICS.topic },
  ...(owner() ? [{ key: 'rule', label: TOPICS.rule }] : []),
  { key: 'documents', label: TOPICS.documents, align: 'right' as const },
]

async function load(): Promise<void> {
  await topics.run(async () => (await api.collections.topics(props.collectionId)).topics)
}

onMounted(load)
</script>

<template>
  <div class="grid gap-4">
    <p class="text-fg-2">{{ TOPICS.lead }}</p>
    <div v-if="props.sources?.length" class="grid gap-2" data-test="topics-help">
      <p class="m-0">
        {{ TOPICS.editRules }}
        <template v-for="(s, i) in props.sources" :key="s.id"><span v-if="i > 0">, </span><RouterLink :to="{ name: 'source', params: { id: s.id } }" data-test="topics-source">{{ s.name }}</RouterLink></template>
      </p>
      <details>
        <summary class="cursor-pointer">{{ TOPICS.howTitle }}</summary>
        <div class="mt-2 grid gap-2">
          <p class="m-0 text-fg-2">{{ TOPICS.how }}</p>
          <pre class="m-0 overflow-x-auto rounded-card bg-sunken p-3 text-small font-mono">{{ TOPICS.example }}</pre>
          <p class="m-0 text-fg-2">{{ TOPICS.after }}</p>
        </div>
      </details>
    </div>
    <AsyncState :loading="topics.loading.value" :error="topics.error.value" :empty="topics.data.value.length === 0" :empty-text="TOPICS.none" @retry="load">
      <DataTable :columns="columns()" :rows="topics.data.value" :row-key="(t) => t.tag" :caption="TOPICS.topic" data-test="topics">
        <template #cell-tag="{ row }"><span class="font-mono">{{ row.tag }}</span></template>
        <template #cell-rule="{ row }">
          <span v-if="row.rules?.length" class="grid gap-0.5">
            <span v-for="r in row.rules" :key="`${r.sourceId}-${r.match}`"><span class="font-mono">{{ r.match }}</span> <span class="text-fg-3">· {{ r.sourceName }}</span></span>
          </span>
          <span v-else class="text-fg-3">{{ TOPICS.noRule }}</span>
        </template>
        <template #cell-documents="{ row }"><span class="tabular" :class="quietZero(row.documents)">{{ formatNumber(row.documents) }}</span></template>
      </DataTable>
    </AsyncState>
  </div>
</template>
