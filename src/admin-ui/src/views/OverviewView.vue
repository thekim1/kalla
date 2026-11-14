<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { api } from '@/api'
import type { Department, Overview, Role } from '@/api/types'
import SetupChecklist from '@/components/collections/SetupChecklist.vue'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import { useLoadable } from '@/stores/loadable'
import { DEPARTMENT_SOURCE_LABEL, OVERVIEW, PAGE, ROLE_LABEL, SETUP } from '@/utils/labels'

// Översikt (admin design §5.4 step 1, §5.5.1): the empty state with Skapa kunskapsbank, Kom igång for each collection
// whose checklist is not done (`GET /api/overview`), and the signed-in user's roles and departments with where each
// came from (identity design §1.5, P0 checklist #9). A platform admin first sees the platform's own first-run step, the
// embedding profile, until it exists (Decisions "Deploy configuration"). The attention items join `GET /api/overview` later.
const auth = useAuthStore()
const router = useRouter()
const overview = useLoadable<Overview | null>(null)
const canCreate = computed(() => auth.hasAnyRole(['rag-platform-admin', 'rag-department-admin', 'rag-editor']))
const hasRole = computed(() => auth.roles.some((r) => r in ROLE_LABEL))
const synced = ref<Department[] | null>(null)
onMounted(() => {
  if (!hasRole.value) return
  void overview.run(() => api.overview())
  void api.organisation.departments().then((d) => (synced.value = d)).catch(() => undefined)
})
const name = computed(() => auth.user?.name ?? auth.user?.email ?? '')
const roles = computed(() => auth.roles.filter((r): r is Role => r in ROLE_LABEL))
const departments = computed(() =>
  auth.departmentSources.map((d) => ({ ...d, name: synced.value ? (synced.value.find((s) => s.costCenterCode === d.code)?.name ?? null) : undefined })),
)
</script>

<template>
  <PageHeader :title="name ? OVERVIEW.welcome(name) : PAGE.overview.title" :description="OVERVIEW.lead" />
  <template v-if="overview.data.value">
    <UiCard v-if="overview.data.value.needsEmbeddingProfile" :title="OVERVIEW.profileTitle" class="mb-6" data-test="needs-profile">
      <p class="mb-4">{{ OVERVIEW.profileText }}</p>
      <UiButton variant="primary" icon="add" data-test="create-profile" @click="router.push({ name: 'platform-models' })">{{ OVERVIEW.profileAction }}</UiButton>
    </UiCard>
    <UiCard v-if="!overview.data.value.hasCollections" :title="SETUP.title" class="mb-6" data-test="empty">
      <p class="mb-4">{{ SETUP.empty }}</p>
      <UiButton v-if="canCreate" variant="primary" icon="add" data-test="create" @click="router.push({ name: 'collections' })">{{ SETUP.create }}</UiButton>
    </UiCard>
    <div v-else-if="overview.data.value.setup.length" class="mb-6 grid gap-6">
      <SetupChecklist v-for="s in overview.data.value.setup" :key="s.collectionId" :setup="s" show-name />
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
