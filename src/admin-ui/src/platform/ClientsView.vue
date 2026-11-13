<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '@/api'
import type { ApiKey, Client, ClientSummary } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import SelectField from '@/components/ui/SelectField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiTabs from '@/components/ui/UiTabs.vue'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatDate, formatDateTime, formatRelative } from '@/utils/format'
import { COMMON, LEVEL_LABEL, PLATFORM, PLATFORM_CLIENTS } from '@/utils/labels'
import ClientCreateDrawer from './ClientCreateDrawer.vue'
import ClientDrawer from './ClientDrawer.vue'
import { KEY_STATUS_TONE } from './clients'

// Apps and keys (admin design §5.9, §5.9.1, platform admins): the apps with Add app and a drawer per app, and All keys,
// every key with its app (a row opens its app's drawer at the key). `?tab=keys` opens All keys (the old Keys page's
// address redirects there); `?connect=<assistant id>` (the assistant page's Connect a new app) opens Add app with that
// assistant ticked.
type Tab = 'apps' | 'keys'
const L = PLATFORM_CLIENTS
const ui = useUiStore()
const route = useRoute()
const router = useRouter()
const tab = ref<Tab>(route.query.tab === 'keys' ? 'keys' : 'apps')
const tabs = [
  { value: 'apps' as const, label: L.tabs.apps },
  { value: 'keys' as const, label: L.tabs.keys },
]
const clients = useLoadable<ClientSummary[]>([])
const keys = useLoadable<ApiKey[]>([])
const statusFilter = ref('')
const creating = ref(false)
const preselected = ref<string | null>(null)
const openId = ref<string | null>(null)
const openKeyId = ref<string | null>(null)
const drawerOpen = ref(false)
const columns: Column[] = [
  { key: 'name', label: L.columns.name },
  { key: 'kind', label: L.columns.kind },
  { key: 'ceiling', label: L.columns.ceiling },
  { key: 'enabled', label: L.columns.enabled },
  { key: 'activeKeys', label: L.columns.activeKeys, align: 'right' },
  { key: 'lastUsed', label: L.columns.lastUsed },
]
const keyColumns: Column[] = [
  { key: 'name', label: L.allKeys.columns.name },
  { key: 'app', label: L.allKeys.columns.app },
  { key: 'status', label: L.allKeys.columns.status },
  { key: 'expiresAt', label: L.allKeys.columns.expiresAt },
  { key: 'lastUsed', label: L.allKeys.columns.lastUsed },
]
const statusOptions = [
  { value: '', label: L.allKeys.anyStatus },
  ...Object.entries(L.keyStatus).map(([value, label]) => ({ value, label })),
]
const shownKeys = computed(() => keys.data.value.filter((k) => !statusFilter.value || k.status === statusFilter.value))

async function load(): Promise<void> {
  await clients.run(() => api.clients.list())
}

async function loadKeys(): Promise<void> {
  await keys.run(() => api.keys.list())
}

/** After a change in a drawer: the list on show now, the other when it is next opened. */
function reload(): void {
  keys.loaded.value = false
  void load()
  if (tab.value === 'keys') void loadKeys()
}

function open(client: ClientSummary): void {
  openId.value = client.id
  openKeyId.value = null
  drawerOpen.value = true
}

function openKey(key: ApiKey): void {
  openId.value = key.clientId
  openKeyId.value = key.id
  drawerOpen.value = true
}

function created(client: Client): void {
  ui.notify(L.created(client.name))
  reload()
}

function add(assistantId: string | null = null): void {
  preselected.value = assistantId
  creating.value = true
}

watch(tab, (value) => {
  if (value === 'keys' && !keys.loaded.value) void loadKeys()
  // The address names the tab, so a link or a reload opens the same one.
  void router.replace({ query: { ...route.query, tab: value === 'keys' ? 'keys' : undefined } })
}, { immediate: false })

onMounted(() => {
  void load()
  if (tab.value === 'keys') void loadKeys()
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
  <UiTabs id="clients" v-model="tab" :tabs="tabs" :label="L.tabs.label" class="mb-6" />
  <div v-if="tab === 'apps'" id="clients-panel-apps" role="tabpanel" aria-labelledby="clients-tab-apps">
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
  </div>
  <div v-else id="clients-panel-keys" role="tabpanel" aria-labelledby="clients-tab-keys" class="grid gap-4" data-test="all-keys">
    <div class="max-w-xs">
      <SelectField id="all-keys-status" v-model="statusFilter" :label="L.allKeys.status" :options="statusOptions" />
    </div>
    <AsyncState
      :loading="keys.loading.value" :error="keys.error.value" :empty="shownKeys.length === 0"
      :empty-text="keys.data.value.length === 0 ? L.allKeys.empty : L.allKeys.noMatch" @retry="loadKeys"
    >
      <DataTable :columns="keyColumns" :rows="shownKeys" :row-key="(k) => k.id" :caption="L.tabs.keys" clickable @row-click="openKey">
        <template #cell-name="{ row }">
          <span class="block font-medium">{{ row.name }}</span>
          <span class="block font-mono text-caption whitespace-nowrap text-fg-3">{{ row.prefix }}</span>
        </template>
        <template #cell-app="{ row }">{{ row.clientName }}</template>
        <template #cell-status="{ row }"><UiBadge :tone="KEY_STATUS_TONE[row.status] ?? 'neutral'" :label="L.keyStatus[row.status] ?? row.status" /></template>
        <template #cell-expiresAt="{ row }">{{ row.expiresAt ? formatDate(row.expiresAt) : L.keys.never }}</template>
        <template #cell-lastUsed="{ row }">
          <span :title="row.lastUsedAt ? formatDateTime(row.lastUsedAt) : undefined">{{ row.lastUsedAt ? formatRelative(row.lastUsedAt) : L.neverUsed }}</span>
        </template>
      </DataTable>
    </AsyncState>
  </div>
  <ClientCreateDrawer v-model:open="creating" :assistant-id="preselected" @created="created" />
  <ClientDrawer v-model:open="drawerOpen" :client-id="openId" :key-id="openKeyId" @saved="reload" />
</template>
