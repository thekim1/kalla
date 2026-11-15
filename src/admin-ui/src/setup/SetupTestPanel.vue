<script setup lang="ts">
import { computed } from 'vue'
import type { SetupTest } from '@/api/types'
import AppIcon from '@/components/ui/AppIcon.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { COMMON, SETUP_WIZARD } from '@/utils/labels'
import { setupTestMessage } from '@/utils/messages'
import { TEST_WINDOW_NAME } from './testWindow'

// The test sign-in's result (admin design §5.11 step 3): who signed in, what the token carried, and whether that person is
// a platform admin, with the claims that were read. `blockedUrl`: the browser blocked the window, so the person opens it.
const props = defineProps<{ test: SetupTest | null; stale: boolean; platformAdminGroups: string[]; blockedUrl: string | null }>()
defineEmits<{ (e: 'cancel'): void }>()
const L = SETUP_WIZARD.result
const S = SETUP_WIZARD.signIn

const result = computed(() => props.test?.result ?? null)
const list = (values: string[]) => (values.length ? values.join(', ') : L.none)
</script>

<template>
  <div aria-live="polite" class="grid gap-3" data-test="test-panel">
    <template v-if="test?.status === 'pending'">
      <div v-if="blockedUrl" class="grid gap-2 rounded-control bg-warn-soft p-3 text-small text-warn" data-test="popup-blocked">
        <p class="flex items-center gap-2 font-medium"><AppIcon name="warning" :size="18" filled />{{ S.popupBlocked }}</p>
        <a :href="blockedUrl" :target="TEST_WINDOW_NAME" class="font-medium text-fg underline">{{ S.openWindow }} <span class="sr-only">{{ COMMON.opensInNewTab }}</span></a>
      </div>
      <div class="flex flex-wrap items-center gap-3" data-test="test-pending">
        <p role="status" class="flex items-center gap-2 text-fg-2"><AppIcon name="progress_activity" class="animate-spin" />{{ S.waiting }}</p>
        <UiButton variant="quiet" size="sm" data-test="test-cancel" @click="$emit('cancel')">{{ S.cancel }}</UiButton>
      </div>
    </template>
    <InlineError v-else-if="test?.status === 'incomplete'" tone="warn" :message="L.incomplete" data-test="test-incomplete" />
    <template v-else-if="test">
      <InlineError v-if="stale && test.status === 'passed'" tone="warn" :message="L.stale" data-test="test-stale" />
      <p v-if="test.status === 'passed'" class="flex items-center gap-2 font-medium text-ok" data-test="test-passed"><AppIcon name="check_circle" filled />{{ L.passed }}</p>
      <InlineError v-else :title="L.failed" :message="setupTestMessage(test.reason ?? '')" data-test="test-failed" />
      <dl v-if="result" data-test="test-result">
        <DetailRow :label="L.name">{{ result.name ?? L.none }}</DetailRow>
        <DetailRow :label="L.subject"><span class="font-mono text-small">{{ result.subject }}</span></DetailRow>
        <DetailRow :label="L.roles">{{ list(result.roles) }}</DetailRow>
        <DetailRow :label="L.groups">
          {{ list(result.groups) }}
          <span v-if="result.groupCount > result.groups.length" class="text-small text-fg-3" data-test="groups-capped">({{ L.groupsCapped(result.groups.length, result.groupCount) }})</span>
        </DetailRow>
        <DetailRow :label="L.departments">{{ list(result.departmentCodes) }}</DetailRow>
        <DetailRow :label="L.isAdmin">
          <span class="font-medium" :class="result.isPlatformAdmin ? 'text-ok' : 'text-danger'" data-test="is-admin">{{ result.isPlatformAdmin ? COMMON.yes : COMMON.no }}</span>
          <span class="block text-small text-fg-3" data-test="claims-read">{{ L.claimRead(result.claimsRead.role, result.claimsRead.group) }}</span>
        </DetailRow>
      </dl>
      <div v-if="result && !result.isPlatformAdmin && test.status === 'passed'" class="grid gap-1 text-small" data-test="not-admin-help">
        <p>{{ L.notAdminHelp(result.claimsRead.role) }}</p>
        <p v-if="platformAdminGroups.length">{{ L.notAdminGroups(platformAdminGroups.join(', ')) }}</p>
      </div>
    </template>
  </div>
</template>
