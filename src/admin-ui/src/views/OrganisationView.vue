<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { api } from '@/api'
import type { Department, OrgSyncStatus } from '@/api/types'
import AsyncState from '@/components/ui/AsyncState.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import InlineError from '@/components/ui/InlineError.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useLoadable } from '@/stores/loadable'
import { useAuthStore } from '@/stores/auth'
import { useUiStore } from '@/stores/ui'
import { formatDateTime, formatRelative } from '@/utils/format'
import { gatewayHref } from '@/utils/gateway'
import { COMMON, ORGANISATION, PAGE, PLATFORM } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// The organisation mirror (admin design §4.10): departments in the user's scope with their teams, the gateway's member
// groups and how their knowledge bases are shared (admin design §5.10: what the generic Organisation page showed). Platform
// admins can sync now, see when the sync last ran (P0 checklist #11) and open the gateway's Organisation page, where the
// member groups are edited (admin design §2.3).
const auth = useAuthStore()
const ui = useUiStore()
const departments = useLoadable<Department[]>([])
const status = ref<OrgSyncStatus | null>(null)
const syncing = ref(false)
const syncError = ref<unknown>(null)

const columns: Column[] = [
  { key: 'name', label: ORGANISATION.name },
  { key: 'code', label: ORGANISATION.code },
  { key: 'teams', label: ORGANISATION.teams },
  { key: 'memberGroups', label: PLATFORM.organisation.memberGroups },
  { key: 'sharing', label: PLATFORM.columns.sharing },
  { key: 'status', label: ORGANISATION.status },
  { key: 'syncedAt', label: ORGANISATION.syncedAt },
]

/** The gateway's Organisation page, from the sync's admin URL when it is an http(s) address. */
const gatewayPageHref = computed(() => gatewayHref(status.value?.adminUrl, '/organisation'))

const failingSince = computed(() =>
  status.value && status.value.consecutiveFailures >= 3 ? formatDateTime(status.value.lastSuccessAt ?? status.value.lastRunAt) : null,
)

async function load(): Promise<void> {
  await departments.run(() => api.organisation.departments())
  if (auth.can('admin')) status.value = await api.organisation.syncStatus().catch(() => null)
}

async function syncNow(): Promise<void> {
  syncing.value = true
  syncError.value = null
  try {
    const result = await api.organisation.sync()
    ui.notify(ORGANISATION.synced(result.departments, result.teams))
    await load()
  } catch (e) {
    syncError.value = e
  } finally {
    syncing.value = false
  }
}

onMounted(load)
</script>

<template>
  <PageHeader :title="PAGE.organisation.title" :description="ORGANISATION.lead">
    <UiButton v-if="gatewayPageHref" :href="gatewayPageHref" target="_blank" rel="noopener noreferrer" icon="open_in_new" data-test="gateway-page">
      {{ PLATFORM.organisation.gatewayLink }} <span class="sr-only">{{ COMMON.opensInNewTab }}</span>
    </UiButton>
    <template v-if="auth.can('admin') && status?.enabled">
      <span v-if="status.lastSuccessAt" class="text-small text-fg-3">{{ ORGANISATION.lastRun(formatRelative(status.lastSuccessAt)) }}</span>
      <UiButton icon="sync" :loading="syncing" @click="syncNow">{{ ORGANISATION.syncNow }}</UiButton>
    </template>
  </PageHeader>
  <p v-if="failingSince" role="alert" class="mb-4 rounded-card bg-warn-soft p-4 text-warn">{{ ORGANISATION.failing(failingSince) }}</p>
  <InlineError v-if="syncError" :message="problemMessage(syncError)" class="mb-4" />
  <AsyncState :loading="departments.loading.value" :error="departments.error.value" :empty="departments.data.value.length === 0" :empty-text="ORGANISATION.empty" @retry="load">
    <DataTable :columns="columns" :rows="departments.data.value" :row-key="(d) => d.id" :caption="ORGANISATION.departments">
      <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
      <template #cell-code="{ row }"><span class="tabular">{{ row.costCenterCode }}</span></template>
      <template #cell-teams="{ row }">
        <span v-if="row.teams.length">{{ row.teams.map((t: Department['teams'][number]) => t.name).join(', ') }}</span>
        <span v-else class="text-fg-3">{{ ORGANISATION.noTeams }}</span>
      </template>
      <template #cell-memberGroups="{ row }">
        <span v-if="row.memberGroups?.length" lang="sv">{{ row.memberGroups.join(', ') }}</span>
        <span v-else class="text-fg-3">{{ PLATFORM.organisation.noMemberGroups }}</span>
      </template>
      <template #cell-sharing="{ row }">
        <template v-if="row.sharing">
          <span class="block">{{ PLATFORM.organisation.summary(row.sharing.department, row.sharing.selected, row.sharing.municipality) }}</span>
          <span v-if="row.sharing.sharedWithDepartments" class="block text-small text-fg-3">{{ PLATFORM.organisation.sharedWith }}: {{ row.sharing.sharedWithDepartments }}</span>
        </template>
      </template>
      <template #cell-status="{ row }"><UiBadge :tone="row.isActive ? 'ok' : 'neutral'" :label="row.isActive ? COMMON.active : COMMON.inactive" /></template>
      <template #cell-syncedAt="{ row }"><span class="tabular text-fg-2">{{ formatDateTime(row.syncedAt) }}</span></template>
    </DataTable>
  </AsyncState>
</template>
