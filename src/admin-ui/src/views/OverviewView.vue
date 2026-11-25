<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRouter } from 'vue-router'
import { api } from '@/api'
import type { Department, Overview, Role, SyncHealth } from '@/api/types'
import SetupChecklist from '@/components/collections/SetupChecklist.vue'
import DeploymentNotices from '@/components/overview/DeploymentNotices.vue'
import NeedsAttention from '@/components/overview/NeedsAttention.vue'
import SecretExpiryNotices from '@/components/overview/SecretExpiryNotices.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import { useAuthStore } from '@/stores/auth'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatNumber, formatRelative, formatUsdCost, partOfDay, quietZero } from '@/utils/format'
import { DEPARTMENT_SOURCE_LABEL, OVERVIEW, ROLE_LABEL, SETUP, type Tone } from '@/utils/labels'
import { readStoredIds, writeStoredIds } from '@/utils/storedIds'

// Home (admin design §5.5.1, "P1's Home"), top to bottom: for a platform admin, a reminder per client secret stored by the setup
// wizard that expires within 30 days (§5.11) and the deployment notices (retired variables the stack still sets), each hideable in
// this browser; the platform's first-run step for a platform admin, the
// embedding profile, until it exists (Decisions "Deploy configuration"); Live, a card per published assistant over the
// last 7 days linking to its Usage tab; Health, the runs of the last 24 hours per department, the job queue and the
// organisation and model syncs; Get started, the empty state with Create knowledge base or a checklist per collection not
// done (folded when its knowledge base is published, each hideable in this browser); and the signed-in user's roles and
// departments with where each came from (identity design §1.5, P0 checklist #9). The attention items join in P2.
const auth = useAuthStore()
const router = useRouter()
const overview = useLoadable<Overview | null>(null)
const ui = useUiStore()
const canCreate = computed(() => auth.can('edit'))
const hasRole = computed(() => auth.roles.some((r) => r in ROLE_LABEL))
const synced = ref<Department[] | null>(null)
/** The overview, and the menu's attention count from it. */
function loadOverview(): void {
  void overview.run(() => api.overview()).then(() => {
    if (overview.data.value) ui.attention = overview.data.value.attention.length
  })
}
onMounted(() => {
  if (!hasRole.value) return
  loadOverview()
  void api.organisation.departments().then((d) => (synced.value = d)).catch(() => undefined)
})
// The greeting (admin design §5.7): "Good morning, Anna" by the hour in Europe/Stockholm, and under it the departments
// and roles, so the person sees at once whose page this is.
const firstName = computed(() => auth.user?.name?.trim().split(/\s+/)[0] || null)
const greeting = computed(() => OVERVIEW.greeting(partOfDay(new Date()), firstName.value))
const roles = computed(() => auth.roles.filter((r): r is Role => r in ROLE_LABEL))
const departments = computed(() =>
  auth.departmentSources.map((d) => ({ ...d, name: synced.value ? (synced.value.find((s) => s.costCenterCode === d.code)?.name ?? null) : undefined })),
)

/** The departments under the greeting: a synced name (Swedish), else the code. */
const departmentNames = computed(() => departments.value.map((d) => ({ text: d.name ?? d.code, sv: !!d.name })))
const roleNames = computed(() => roles.value.map((r) => ROLE_LABEL[r]))
const identity = computed(() => OVERVIEW.identity(departmentNames.value.map((d) => d.text), roleNames.value))
const live = computed(() => overview.data.value?.live ?? [])
/** Assistants in use, most asked first; a row opens the assistant's Usage tab. */
const liveRows = computed(() => [...live.value].sort((a, b) => b.questions - a.questions || a.name.localeCompare(b.name)))
const liveColumns: Column[] = [
  { key: 'name', label: OVERVIEW.assistant },
  { key: 'questions', label: OVERVIEW.questions, align: 'right' },
  { key: 'notFound', label: OVERVIEW.notFound, align: 'right' },
  { key: 'thumbsUp', label: OVERVIEW.thumbsUp, align: 'right' },
  { key: 'thumbsDown', label: OVERVIEW.thumbsDown, align: 'right' },
  { key: 'cost', label: OVERVIEW.cost, align: 'right' },
]
const usageOf = (a: { assistantId: string }) => ({ name: 'assistant', params: { id: a.assistantId }, query: { tab: 'usage' } })
const accessOpen = ref(false)
/** Health's dots: green while fine, amber with remarks, red on a failure (the words say it too); grey for nothing yet (rule 8). */
const DOT: Record<'ok' | 'warn' | 'danger' | 'none', string> = { ok: 'bg-ok', warn: 'bg-warn', danger: 'bg-danger', none: 'bg-fg-3' }
const runTotals = computed(() => {
  const t = { succeeded: 0, degraded: 0, failed: 0, running: 0 }
  for (const d of overview.data.value?.health.departments ?? []) for (const k of Object.keys(t) as (keyof typeof t)[]) t[k] += d.runs[k] ?? 0
  return t
})
const noRuns = computed(() => Object.values(runTotals.value).every((n) => n === 0))
const runsTone = computed(() => (runTotals.value.failed ? 'danger' : runTotals.value.degraded ? 'warn' : noRuns.value ? 'none' : 'ok'))
const runsSummary = computed(() => {
  const parts = RUN_OUTCOMES.filter((o) => runTotals.value[o.key]).map((o) => o.label(runTotals.value[o.key]))
  return parts.length ? parts.join(', ') : OVERVIEW.noRunsShort
})
const quiet = computed(() => live.value.every((a) => a.questions === 0))

const RUN_OUTCOMES = [
  { key: 'succeeded', tone: 'ok', label: OVERVIEW.runsSucceeded },
  { key: 'degraded', tone: 'warn', label: OVERVIEW.runsDegraded },
  { key: 'failed', tone: 'danger', label: OVERVIEW.runsFailed },
  { key: 'running', tone: 'accent', label: OVERVIEW.runsRunning },
] as const satisfies readonly { key: string; tone: Tone; label: (n: number) => string }[]

// The Models page is the platform admins' (platform/routes.ts); Organisation is everyone's.
const syncs = computed(() => {
  const health = overview.data.value?.health
  if (!health) return []
  return [
    { key: 'organisation', label: OVERVIEW.organisationSync, sync: health.organisationSync, to: { name: 'organisation' } },
    { key: 'models', label: OVERVIEW.modelSync, sync: health.modelSync, to: auth.can('admin') ? { name: 'platform-models' } : null },
  ]
})
/** A sync that never succeeded is grey until it fails, not green. */
const syncTone = (sync: SyncHealth) => (sync.consecutiveFailures ? 'danger' : sync.lastSuccessAt ? 'ok' : 'none')
const lastSuccess = (sync: SyncHealth) => (sync.lastSuccessAt ? OVERVIEW.lastSuccess(formatRelative(sync.lastSuccessAt)) : OVERVIEW.noSuccess)

// Hide is remembered per browser (§5.5.1 item 3); without storage it lasts until the page is left.
const HIDDEN_STORAGE_KEY = 'ume-home-hidden-setup'
const writeHidden = (ids: string[]) => writeStoredIds(HIDDEN_STORAGE_KEY, ids)
const hidden = ref<string[]>(readStoredIds(HIDDEN_STORAGE_KEY))
const setups = computed(() => overview.data.value?.setup ?? [])
const shownSetups = computed(() => setups.value.filter((s) => !hidden.value.includes(s.collectionId)))
const hiddenCount = computed(() => setups.value.length - shownSetups.value.length)
function hide(collectionId: string): void {
  // Ids of checklists no longer listed (done, or the collection gone) are dropped on the way.
  hidden.value = [...hidden.value.filter((id) => setups.value.some((s) => s.collectionId === id)), collectionId]
  writeHidden(hidden.value)
}
function showHidden(): void {
  hidden.value = []
  writeHidden([])
}
</script>

<template>
  <!-- Home (admin design §5.5.1, UX pass UX6 C), top to bottom: the greeting with Your access, the notices, Needs
       attention, Get started, Assistants in use and Health; what needs the person comes before what runs. -->
  <PageHeader :title="greeting">
    <!-- The departments and roles under the greeting; the department names are Swedish (admin design §5.7; #1036). -->
    <template #description>
      <span data-test="identity"><template v-for="(d, i) in departmentNames" :key="i"><template v-if="i > 0">{{ OVERVIEW.listSeparator }}</template><span :lang="d.sv ? 'sv' : undefined">{{ d.text }}</span></template><template v-if="departmentNames.length && roleNames.length">{{ OVERVIEW.partSeparator }}</template>{{ roleNames.join(OVERVIEW.listSeparator) }}</span>
      <template v-if="identity">{{ OVERVIEW.partSeparator }}</template>
      <button type="button" class="text-accent-ink underline" aria-haspopup="dialog" data-test="your-access" @click="accessOpen = true">{{ OVERVIEW.access }}</button>
    </template>
  </PageHeader>
  <!-- Without a Källa role there is nothing else on the page: say so here, not only in Your access. -->
  <p v-if="!hasRole" class="mb-6" data-test="no-role">{{ OVERVIEW.noRoles }}</p>
  <template v-if="overview.data.value">
    <SecretExpiryNotices v-if="auth.isPlatformAdmin && overview.data.value.secretExpiry.length" :entries="overview.data.value.secretExpiry" class="mb-6" />
    <DeploymentNotices v-if="auth.isPlatformAdmin && overview.data.value.deploymentNotices.length" :notices="overview.data.value.deploymentNotices" class="mb-6" />
    <NeedsAttention :items="overview.data.value.attention" class="mb-6" @changed="loadOverview" />

    <!-- Get started: the embedding profile's first-run step first while it applies, then the checklists. -->
    <UiCard v-if="overview.data.value.needsEmbeddingProfile" :title="OVERVIEW.profileTitle" class="mb-6" data-test="needs-profile">
      <p class="mb-4">{{ OVERVIEW.profileText }}</p>
      <UiButton variant="primary" icon="add" data-test="create-profile" @click="router.push({ name: 'platform-models' })">{{ OVERVIEW.profileAction }}</UiButton>
    </UiCard>
    <UiCard v-if="!overview.data.value.hasCollections" :title="SETUP.title" class="mb-6" data-test="empty">
      <p class="mb-4">{{ SETUP.empty }}</p>
      <UiButton v-if="canCreate" variant="primary" icon="add" data-test="create" @click="router.push({ name: 'collections' })">{{ SETUP.create }}</UiButton>
    </UiCard>
    <div v-else-if="setups.length" class="mb-6 grid gap-6" data-test="get-started">
      <SetupChecklist v-for="s in shownSetups" :key="s.collectionId" :setup="s" show-name foldable>
        <template #actions>
          <UiButton variant="quiet" size="sm" :aria-label="OVERVIEW.hideFor(s.name)" data-test="hide" @click="hide(s.collectionId)">{{ OVERVIEW.hide }}</UiButton>
        </template>
      </SetupChecklist>
      <div v-if="hiddenCount">
        <UiButton variant="quiet" size="sm" data-test="show-hidden" @click="showHidden">{{ OVERVIEW.showHidden(hiddenCount) }}</UiButton>
      </div>
    </div>

    <!-- Assistants in use, the last 7 days: a table, the most asked first, zeros grey (rule 8); cards on a phone. -->
    <UiCard v-if="live.length" :title="OVERVIEW.live" :meta="OVERVIEW.liveMeta" heading-id="live-h" class="mb-6" data-test="live">
      <p v-if="quiet" class="mb-3 text-fg-2" data-test="live-empty">{{ OVERVIEW.liveEmpty }}</p>
      <DataTable :columns="liveColumns" :rows="liveRows" :row-key="(a) => a.assistantId" :caption="OVERVIEW.liveCaption" :row-to="usageOf">
        <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
        <template #cell-questions="{ row }"><span class="tabular" :class="quietZero(row.questions)" data-test="live-questions">{{ formatNumber(row.questions) }}</span></template>
        <template #cell-notFound="{ row }"><span class="tabular" :class="quietZero(row.notFound)">{{ formatNumber(row.notFound) }}</span></template>
        <template #cell-thumbsUp="{ row }"><span class="tabular" :class="quietZero(row.thumbsUp)">{{ formatNumber(row.thumbsUp) }}</span></template>
        <template #cell-thumbsDown="{ row }"><span class="tabular" :class="quietZero(row.thumbsDown)">{{ formatNumber(row.thumbsDown) }}</span></template>
        <template #cell-cost="{ row }"><span class="tabular" :class="quietZero(row.costUsd)" data-test="live-cost">{{ formatUsdCost(row.costUsd) }}</span></template>
      </DataTable>
    </UiCard>

    <!-- Health: one line of four states; a state that fails turns amber or red, in words as well as colour. -->
    <section class="mb-6" aria-labelledby="health-h" data-test="health">
      <div class="flex flex-wrap items-center gap-x-5 gap-y-2">
        <h2 id="health-h" class="font-medium">{{ OVERVIEW.health }}</h2>
        <span class="inline-flex items-center gap-1.5" data-test="health-runs-total">
          <span class="size-2 rounded-full" :class="DOT[runsTone]" aria-hidden="true" :data-test="`runs-dot-${runsTone}`" />
          <!-- One flex item, so the gap doesn't fall before the colon. -->
          <span><RouterLink :to="{ name: 'collections' }" class="text-accent-ink underline">{{ OVERVIEW.fetches24h }}</RouterLink>: <span :class="{ 'text-fg-3': noRuns }" data-test="health-runs-summary">{{ runsSummary }}</span></span>
        </span>
        <span class="inline-flex items-center gap-1.5" data-test="health-queue">
          <span class="size-2 rounded-full" :class="DOT.ok" aria-hidden="true" />
          <span>{{ OVERVIEW.queue }}: {{ OVERVIEW.queueWaiting(overview.data.value.health.queue.waiting) }}</span>
          <span v-if="overview.data.value.health.queue.oldestQueuedAt" class="text-fg-2">· {{ OVERVIEW.queueOldest(formatRelative(overview.data.value.health.queue.oldestQueuedAt)) }}</span>
        </span>
        <span v-for="s in syncs" :key="s.key" class="inline-flex flex-wrap items-center gap-1.5" :data-test="`health-${s.key}`">
          <span class="size-2 rounded-full" :class="DOT[syncTone(s.sync)]" aria-hidden="true" :data-test="`sync-dot-${syncTone(s.sync)}`" />
          <RouterLink v-if="s.to" :to="s.to" class="text-accent-ink underline">{{ s.label }}</RouterLink>
          <span v-else>{{ s.label }}</span>
          <span>{{ lastSuccess(s.sync) }}</span>
          <UiBadge v-if="s.sync.consecutiveFailures" tone="danger" :label="OVERVIEW.failures(s.sync.consecutiveFailures)" data-test="sync-failures" />
        </span>
      </div>
      <!-- The per-department counts behind the fetches. -->
      <details v-if="overview.data.value.health.departments.length" class="mt-2 text-small" data-test="health-by-department">
        <summary class="cursor-pointer py-1 text-fg-2 max-md:py-3">{{ OVERVIEW.byDepartment }}</summary>
        <ul class="mt-2 flex flex-col gap-2" data-test="health-runs">
          <li v-for="d in overview.data.value.health.departments" :key="d.departmentId" class="flex flex-wrap items-center gap-x-2 gap-y-1">
            <span class="min-w-0 break-words font-medium">{{ d.departmentName }}</span>
            <template v-for="o in RUN_OUTCOMES" :key="o.key">
              <UiBadge v-if="d.runs[o.key]" :tone="o.tone" :label="o.label(d.runs[o.key])" :data-test="`runs-${o.key}`" />
            </template>
          </li>
        </ul>
      </details>
    </section>
  </template>

  <!-- Your access (§5.5.1 item 1): the roles and departments the sign-in gave, and where each came from. -->
  <UiDrawer v-model:open="accessOpen" :title="OVERVIEW.access" data-test="access-drawer">
    <dl class="grid gap-5">
      <div>
        <dt class="text-small font-medium text-fg-2">{{ OVERVIEW.roles }}</dt>
        <dd class="mt-2">
          <ul v-if="roles.length" class="flex flex-wrap gap-2" data-test="roles">
            <li v-for="role in roles" :key="role"><UiBadge tone="accent" :label="ROLE_LABEL[role]" /></li>
          </ul>
          <p v-else class="text-fg-2">{{ OVERVIEW.noRoles }}</p>
        </dd>
      </div>
      <div>
        <dt class="text-small font-medium text-fg-2">{{ OVERVIEW.departments }}</dt>
        <dd class="mt-2">
          <p v-if="auth.isPlatformAdmin" class="mb-2 text-small text-fg-3">{{ OVERVIEW.allDepartments }}</p>
          <ul v-if="departments.length" class="flex flex-col gap-1.5" data-test="departments">
            <li v-for="d in departments" :key="d.code" class="flex flex-wrap items-center gap-x-2 gap-y-1">
              <span v-if="d.name" class="font-medium" lang="sv">{{ d.name }} <span class="tabular text-small font-normal text-fg-3">{{ d.code }}</span></span>
              <span v-else-if="d.name === null" class="font-medium">{{ OVERVIEW.unknownDepartment(d.code) }}</span>
              <span v-else class="tabular font-medium">{{ d.code }}</span>
              <UiBadge :tone="d.source === 'group' ? 'ok' : 'neutral'" :label="DEPARTMENT_SOURCE_LABEL[d.source]" />
            </li>
          </ul>
          <p v-else class="text-fg-2">{{ OVERVIEW.noDepartments }}</p>
        </dd>
      </div>
    </dl>
  </UiDrawer>
</template>
