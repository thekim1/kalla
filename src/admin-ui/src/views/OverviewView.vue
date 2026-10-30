<script setup lang="ts">
import { computed } from 'vue'
import type { Role } from '@/api/types'
import PageHeader from '@/components/ui/PageHeader.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import { DEPARTMENT_SOURCE_LABEL, OVERVIEW, PAGE, ROLE_LABEL } from '@/utils/labels'

// P0: the signed-in user's roles and departments with where each came from (identity design §1.5, P0 checklist #9).
// The attention cards of admin design §5.5 join with the journey pages.
const auth = useAuthStore()
const name = computed(() => auth.user?.name ?? auth.user?.email ?? '')
const roles = computed(() => auth.roles.filter((r): r is Role => r in ROLE_LABEL))
const departments = computed(() => auth.departmentSources)
</script>

<template>
  <PageHeader :title="name ? OVERVIEW.welcome(name) : PAGE.overview.title" :description="OVERVIEW.lead" />
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
            <li v-for="d in departments" :key="d.code" class="flex items-center gap-2">
              <span class="tabular font-medium">{{ d.code }}</span>
              <UiBadge :tone="d.source === 'group' ? 'ok' : 'neutral'" :label="DEPARTMENT_SOURCE_LABEL[d.source]" />
            </li>
          </ul>
          <p v-else class="text-fg-2">{{ OVERVIEW.noDepartments }}</p>
        </dd>
      </div>
    </dl>
  </UiCard>
</template>
