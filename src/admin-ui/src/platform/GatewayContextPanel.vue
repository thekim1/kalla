<script setup lang="ts">
import { onMounted, ref } from 'vue'
import UiCard from '@/components/ui/UiCard.vue'
import { PLATFORM } from '@/utils/labels'
import { gatewayContext, type GatewayContext } from './resources'

// The gateway's version and features from its last context (admin design §4.10); nothing until the sync has read one.
const G = PLATFORM.gatewayModels
const context = ref<GatewayContext | null>(null)

onMounted(async () => {
  context.value = await gatewayContext()
})
</script>

<template>
  <UiCard v-if="context" :title="G.gateway" heading-id="gateway-context-title" data-test="gateway-context">
    <dl class="grid grid-cols-[auto_1fr] gap-x-6 gap-y-1">
      <dt class="text-fg-2">{{ G.version }}</dt>
      <dd class="tabular" data-test="gateway-version">{{ context.version ?? G.unknownVersion }}</dd>
      <dt class="text-fg-2">{{ G.features }}</dt>
      <dd data-test="gateway-features">{{ context.features.length ? context.features.join(', ') : G.noFeatures }}</dd>
    </dl>
  </UiCard>
</template>
