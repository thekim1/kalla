<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Client, ClientSummary } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { COMMON, LEVEL_LABEL, PLATFORM, PLATFORM_CLIENTS } from '@/utils/labels'
import ClientCreateDrawer from './ClientCreateDrawer.vue'
import ClientDrawer from './ClientDrawer.vue'

// Clients (admin design §5.9, platform admins): the list, Add client, and a drawer per client with its keys.
const L = PLATFORM_CLIENTS
const ui = useUiStore()
const clients = useLoadable<ClientSummary[]>([])
const creating = ref(false)
const openId = ref<string | null>(null)
const drawerOpen = ref(false)
const columns: Column[] = [
  { key: 'name', label: L.columns.name },
  { key: 'kind', label: L.columns.kind },
  { key: 'ceiling', label: L.columns.ceiling },
  { key: 'enabled', label: L.columns.enabled },
  { key: 'activeKeys', label: L.columns.activeKeys, align: 'right' },
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

onMounted(load)
</script>

<template>
  <PageHeader :title="PLATFORM.resources.clients.label" :description="L.lead">
    <UiButton variant="primary" icon="add" data-test="add-client" @click="creating = true">{{ L.add }}</UiButton>
  </PageHeader>
  <AsyncState :loading="clients.loading.value" :error="clients.error.value" :empty="clients.data.value.length === 0" :empty-text="L.empty" @retry="load">
    <DataTable :columns="columns" :rows="clients.data.value" :row-key="(c) => c.id" :caption="PLATFORM.resources.clients.label" clickable @row-click="open">
      <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
      <template #cell-kind="{ row }">{{ L.kinds[row.kind]?.title ?? row.kind }}</template>
      <template #cell-ceiling="{ row }">{{ LEVEL_LABEL[row.classificationCeiling as ClientSummary['classificationCeiling']] }}</template>
      <template #cell-enabled="{ row }">{{ row.isEnabled ? COMMON.yes : COMMON.no }}</template>
      <template #cell-activeKeys="{ row }">{{ row.activeKeys }}</template>
    </DataTable>
  </AsyncState>
  <ClientCreateDrawer v-model:open="creating" @created="created" />
  <ClientDrawer v-model:open="drawerOpen" :client-id="openId" @saved="load" />
</template>
