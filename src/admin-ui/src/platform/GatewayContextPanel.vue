<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import UiCard from '@/components/ui/UiCard.vue'
import { formatDateTime } from '@/utils/format'
import { PLATFORM } from '@/utils/labels'
import type { OrgSyncStatus } from '@/api/types'
import { orgSyncStatus } from './resources'

// The gateway's version and features from its last context (admin design §4.10), and the Integration API read-back
// (#654, admin design §4.5, §8.6), both from the organisation sync's status without a gateway call.
const G = PLATFORM.gatewayModels
const status = ref<OrgSyncStatus | null>(null)
const context = computed(() => status.value?.gateway ?? null)
const integration = computed(() => status.value?.integration ?? null)

const readback = computed(() => {
  const i = integration.value
  if (!i) return null
  if (!i.configured) return { state: 'off', text: G.readbackOff }
  if (!i.lastError) return { state: 'on', text: G.readbackOn }
  const reason = G.readbackReasons[i.lastError.reason] ?? G.readbackReasons.failed!
  return { state: 'failed', text: G.readbackFailed(reason, i.lastError.status, formatDateTime(i.lastError.at)) }
})

onMounted(async () => {
  status.value = await orgSyncStatus()
})
</script>

<template>
  <UiCard v-if="context || readback" :title="G.gateway" heading-id="gateway-context-title" data-test="gateway-context">
    <dl class="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1">
      <template v-if="context">
        <dt class="text-fg-2">{{ G.version }}</dt>
        <dd class="tabular" data-test="gateway-version">{{ context.version ?? G.unknownVersion }}</dd>
        <dt class="text-fg-2">{{ G.features }}</dt>
        <dd data-test="gateway-features" :class="{ 'text-fg-3': !context.features.length }">{{ context.features.length ? context.features.join(', ') : G.noFeatures }}</dd>
      </template>
      <template v-if="readback">
        <dt class="text-fg-2">{{ G.readback }}</dt>
        <dd data-test="gateway-readback" :data-state="readback.state">
          {{ readback.text }}
          <span v-if="readback.state === 'failed'" class="block text-small text-fg-2">{{ G.readbackHelp }}</span>
        </dd>
      </template>
    </dl>
  </UiCard>
</template>
