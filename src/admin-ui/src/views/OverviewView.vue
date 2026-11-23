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
import { useAuthStore } from '@/stores/auth'
import { useLoadable } from '@/stores/loadable'
import { useUiStore } from '@/stores/ui'
import { formatNumber, formatRelative, formatUsdCost } from '@/utils/format'
import { DEPARTMENT_SOURCE_LABEL, OVERVIEW, PAGE, ROLE_LABEL, SETUP, type Tone } from '@/utils/labels'
import { readStoredIds, writeStoredIds } from '@/utils/storedIds'

// Home (admin design §5.5.1, "P1's Home"), top to bottom: for a platform admin, a reminder per client secret stored by the setup
// wizard that expires within 30 days (§5.11) and the deployment notices (retired variables the stack still sets), each hideable in
// this browser; the platform's first-run step for a platform admin, the
// embedding profile, until it exists (Decisions "Deploy configuration"); Live, a card per published assistant over the
// last 7 days linking to its Statistics tab; Health, the runs of the last 24 hours per department, the job queue and the
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
onMounted(() => {
  if (!hasRole.value) return
  void overview.run(() => api.overview()).then(() => {
    if (overview.data.value) ui.attention = overview.data.value.attention.length
  })
  void api.organisation.departments().then((d) => (synced.value = d)).catch(() => undefined)
})
const name = computed(() => auth.user?.name ?? auth.user?.email ?? '')
const roles = computed(() => auth.roles.filter((r): r is Role => r in ROLE_LABEL))
const departments = computed(() =>
  auth.departmentSources.map((d) => ({ ...d, name: synced.value ? (synced.value.find((s) => s.costCenterCode === d.code)?.name ?? null) : undefined })),
)

const live = computed(() => overview.data.value?.live ?? [])
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
    { key: 'models', label: OVERVIEW.modelSync, sync: health.modelSync, to: auth.isPlatformAdmin ? { name: 'platform-models' } : null },
  ]
})
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
  <PageHeader :title="name ? OVERVIEW.welcome(name) : PAGE.overview.title" :description="OVERVIEW.lead" />
  <template v-if="overview.data.value">
    <NeedsAttention :items="overview.data.value.attention" class="mb-6" />
    <SecretExpiryNotices v-if="auth.isPlatformAdmin && overview.data.value.secretExpiry.length" :entries="overview.data.value.secretExpiry" class="mb-6" />
    <DeploymentNotices v-if="auth.isPlatformAdmin && overview.data.value.deploymentNotices.length" :notices="overview.data.value.deploymentNotices" class="mb-6" />
    <UiCard v-if="overview.data.value.needsEmbeddingProfile" :title="OVERVIEW.profileTitle" class="mb-6" data-test="needs-profile">
      <p class="mb-4">{{ OVERVIEW.profileText }}</p>
      <UiButton variant="primary" icon="add" data-test="create-profile" @click="router.push({ name: 'platform-models' })">{{ OVERVIEW.profileAction }}</UiButton>
    </UiCard>
    <UiCard v-if="live.length" :title="OVERVIEW.live" :meta="OVERVIEW.liveMeta" heading-id="live-h" class="mb-6" data-test="live">
      <p v-if="quiet" class="mb-4 text-fg-2" data-test="live-empty">{{ OVERVIEW.liveEmpty }}</p>
      <ul class="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
        <li v-for="a in live" :key="a.assistantId">
          <RouterLink
            :to="{ name: 'assistant', params: { id: a.assistantId }, query: { tab: 'statistics' } }"
            class="block h-full rounded-control bg-sunken p-4 hover:bg-hover"
            data-test="live-card"
          >
            <span class="flex flex-wrap items-baseline justify-between gap-x-2">
              <span class="min-w-0 break-words font-medium">{{ a.name }}</span>
              <span class="text-small text-fg-3">{{ OVERVIEW.liveVersion(a.publishedVersion) }}</span>
            </span>
            <dl class="mt-3 grid grid-cols-2 gap-x-4 gap-y-2 text-small">
              <div>
                <dt class="text-fg-3">{{ OVERVIEW.questions }}</dt>
                <dd class="tabular font-medium" data-test="live-questions">{{ formatNumber(a.questions) }}</dd>
              </div>
              <div>
                <dt class="text-fg-3">{{ OVERVIEW.notFound }}</dt>
                <dd class="tabular font-medium">{{ formatNumber(a.notFound) }}</dd>
              </div>
              <div>
                <dt class="text-fg-3">{{ OVERVIEW.thumbsUp }}</dt>
                <dd class="tabular font-medium">{{ formatNumber(a.thumbsUp) }}</dd>
              </div>
              <div>
                <dt class="text-fg-3">{{ OVERVIEW.thumbsDown }}</dt>
                <dd class="tabular font-medium">{{ formatNumber(a.thumbsDown) }}</dd>
              </div>
              <div class="col-span-2">
                <dt class="text-fg-3">{{ OVERVIEW.cost }}</dt>
                <dd class="tabular font-medium" data-test="live-cost">{{ formatUsdCost(a.costUsd) }}</dd>
              </div>
            </dl>
          </RouterLink>
        </li>
      </ul>
    </UiCard>
    <UiCard :title="OVERVIEW.health" heading-id="health-h" class="mb-6" data-test="health">
      <div class="grid gap-5 md:grid-cols-2">
        <section aria-labelledby="health-runs-h">
          <h3 id="health-runs-h" class="text-small font-medium text-fg-2">{{ OVERVIEW.runs }}</h3>
          <ul v-if="overview.data.value.health.departments.length" class="mt-2 flex flex-col gap-2" data-test="health-runs">
            <li v-for="d in overview.data.value.health.departments" :key="d.departmentId" class="flex flex-wrap items-center gap-x-2 gap-y-1">
              <RouterLink :to="{ name: 'collections' }" class="min-w-0 break-words font-medium underline-offset-2 hover:underline">{{ d.departmentName }}</RouterLink>
              <template v-for="o in RUN_OUTCOMES" :key="o.key">
                <UiBadge v-if="d.runs[o.key]" :tone="o.tone" :label="o.label(d.runs[o.key])" :data-test="`runs-${o.key}`" />
              </template>
            </li>
          </ul>
          <p v-else class="mt-2 text-fg-2">{{ OVERVIEW.noRuns }}</p>
        </section>
        <section aria-labelledby="health-queue-h">
          <h3 id="health-queue-h" class="text-small font-medium text-fg-2">{{ OVERVIEW.queue }}</h3>
          <p class="mt-2" data-test="health-queue">
            <span class="font-medium">{{ OVERVIEW.queueWaiting(overview.data.value.health.queue.waiting) }}</span>
            <span v-if="overview.data.value.health.queue.oldestQueuedAt" class="text-fg-2">
              · {{ OVERVIEW.queueOldest(formatRelative(overview.data.value.health.queue.oldestQueuedAt)) }}
            </span>
          </p>
        </section>
        <section v-for="s in syncs" :key="s.key" :aria-labelledby="`health-${s.key}-h`" :data-test="`health-${s.key}`">
          <h3 :id="`health-${s.key}-h`" class="text-small font-medium text-fg-2">
            <RouterLink v-if="s.to" :to="s.to" class="underline-offset-2 hover:underline">{{ s.label }}</RouterLink>
            <template v-else>{{ s.label }}</template>
          </h3>
          <p class="mt-2 flex flex-wrap items-center gap-x-2 gap-y-1">
            <span>{{ lastSuccess(s.sync) }}</span>
            <UiBadge v-if="s.sync.consecutiveFailures" tone="danger" :label="OVERVIEW.failures(s.sync.consecutiveFailures)" data-test="sync-failures" />
          </p>
        </section>
      </div>
    </UiCard>
    <UiCard v-if="!overview.data.value.hasCollections" :title="SETUP.title" class="mb-6" data-test="empty">
      <p class="mb-4">{{ SETUP.empty }}</p>
      <UiButton v-if="canCreate" variant="primary" icon="add" data-test="create" @click="router.push({ name: 'collections' })">{{ SETUP.create }}</UiButton>
    </UiCard>
    <div v-else-if="setups.length" class="mb-6 grid gap-6">
      <SetupChecklist v-for="s in shownSetups" :key="s.collectionId" :setup="s" show-name foldable>
        <template #actions>
          <UiButton variant="quiet" size="sm" :aria-label="OVERVIEW.hideFor(s.name)" data-test="hide" @click="hide(s.collectionId)">{{ OVERVIEW.hide }}</UiButton>
        </template>
      </SetupChecklist>
      <div v-if="hiddenCount">
        <UiButton variant="quiet" size="sm" data-test="show-hidden" @click="showHidden">{{ OVERVIEW.showHidden(hiddenCount) }}</UiButton>
      </div>
    </div>
  </template>
  <UiCard :title="OVERVIEW.access" heading-id="access-h">
    <dl class="grid gap-5 md:grid-cols-2">
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
              <span v-if="d.name" class="font-medium">{{ d.name }} <span class="tabular text-small font-normal text-fg-3">{{ d.code }}</span></span>
              <span v-else-if="d.name === null" class="font-medium">{{ OVERVIEW.unknownDepartment(d.code) }}</span>
              <span v-else class="tabular font-medium">{{ d.code }}</span>
              <UiBadge :tone="d.source === 'group' ? 'ok' : 'neutral'" :label="DEPARTMENT_SOURCE_LABEL[d.source]" />
            </li>
          </ul>
          <p v-else class="text-fg-2">{{ OVERVIEW.noDepartments }}</p>
        </dd>
      </div>
    </dl>
  </UiCard>
</template>
