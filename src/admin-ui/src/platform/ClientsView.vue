<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '@/api'
import type { Client, ClientSummary } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatRelative } from '@/utils/format'
import { COMMON, LEVEL_LABEL, PLATFORM, PLATFORM_CLIENTS } from '@/utils/labels'
import ClientCreateDrawer from './ClientCreateDrawer.vue'
import ClientDrawer from './ClientDrawer.vue'

// Apps and keys (admin design §5.9, §5.9.1, platform admins): the list, Add app, and a drawer per app with its keys and
// assistants. `?connect=<assistant id>` (the assistant page's Connect a new app) opens Add app with that assistant ticked.
const L = PLATFORM_CLIENTS
const ui = useUiStore()
const clients = useLoadable<ClientSummary[]>([])
const route = useRoute()
const router = useRouter()
const creating = ref(false)
const preselected = ref<string | null>(null)
const openId = ref<string | null>(null)
const drawerOpen = ref(false)
const columns: Column[] = [
  { key: 'name', label: L.columns.name },
  { key: 'kind', label: L.columns.kind },
  { key: 'ceiling', label: L.columns.ceiling },
  { key: 'enabled', label: L.columns.enabled },
  { key: 'activeKeys', label: L.columns.activeKeys, align: 'right' },
  { key: 'lastUsed', label: L.columns.lastUsed },
]

async function load(): Promise<void> {
  await clients.run(() => api.clients.list())
}

function open(client: ClientSummary): void {
  openId.value = client.id
  drawerOpen.value = true
}

function created(client: Client): void {
  ui.notify(L.created(client.name))
  void load()
}

function add(assistantId: string | null = null): void {
  preselected.value = assistantId
  creating.value = true
}

onMounted(() => {
  void load()
  const connect = route.query.connect
  if (typeof connect === 'string' && connect) {
    add(connect)
    // The address keeps no trace, so a reload does not reopen the drawer.
    void router.replace({ query: { ...route.query, connect: undefined } })
  }
})
</script>

<template>
  <PageHeader :title="PLATFORM.resources.clients.label" :description="L.lead">
    <UiButton variant="primary" icon="add" data-test="add-client" @click="add()">{{ L.add }}</UiButton>
  </PageHeader>
  <AsyncState :loading="clients.loading.value" :error="clients.error.value" :empty="clients.data.value.length === 0" :empty-text="L.empty" @retry="load">
    <DataTable :columns="columns" :rows="clients.data.value" :row-key="(c) => c.id" :caption="PLATFORM.resources.clients.label" clickable @row-click="open">
      <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
      <template #cell-kind="{ row }">{{ L.kinds[row.kind]?.title ?? row.kind }}</template>
      <template #cell-ceiling="{ row }">{{ LEVEL_LABEL[row.classificationCeiling as ClientSummary['classificationCeiling']] }}</template>
      <template #cell-enabled="{ row }">{{ row.isEnabled ? COMMON.yes : COMMON.no }}</template>
      <template #cell-activeKeys="{ row }">{{ row.activeKeys }}</template>
      <template #cell-lastUsed="{ row }">
        <span :title="row.lastUsedAt ? formatDateTime(row.lastUsedAt) : undefined">{{ row.lastUsedAt ? formatRelative(row.lastUsedAt) : L.neverUsed }}</span>
      </template>
    </DataTable>
  </AsyncState>
  <ClientCreateDrawer v-model:open="creating" :assistant-id="preselected" @created="created" />
  <ClientDrawer v-model:open="drawerOpen" :client-id="openId" @saved="load" />
</template>
