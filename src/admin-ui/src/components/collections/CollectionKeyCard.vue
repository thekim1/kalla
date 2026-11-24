<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { api } from '@/api'
import type { GatewayKeyList } from '@/api/types'
import KeyBudgetStatus from '@/components/gateway/KeyBudgetStatus.vue'
import KeySettings from '@/components/gateway/KeySettings.vue'
import KeyUnavailableBadge from '@/components/gateway/KeyUnavailableBadge.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { gatewayHref } from '@/utils/gateway'
import { GATEWAY_KEY } from '@/utils/labels'

/**
 * The knowledge base Overview's *Gateway key* card (admin design §5.5.2): the key indexing and evaluation run as, by name,
 * its settings read-only (KeySettings, as in the key picker), its status (an unusable key's badge with the reason), its
 * budget status from gateway phase 3, and a link to the gateway's Keys page (it has no address per key); without a key,
 * that the calls are charged to the platform. The key is changed in *Edit name and contact*.
 */
const props = defineProps<{ gatewayKeyId?: string | null; departmentId: string }>()
const list = ref<GatewayKeyList | null>(null)
const key = computed(() => list.value?.keys.find((k) => k.id === props.gatewayKeyId) ?? null)
const link = computed(() => gatewayHref(list.value?.adminUrl, '/keys'))

async function load(): Promise<void> {
  list.value = props.gatewayKeyId ? await api.gateway.keys(props.departmentId).catch(() => null) : null
}
watch(() => props.gatewayKeyId, () => void load())
onMounted(load)
</script>

<template>
  <UiCard :title="GATEWAY_KEY.label" data-test="key-card">
    <p v-if="!gatewayKeyId" class="text-fg-2" data-test="key-none">{{ GATEWAY_KEY.noneWarning }}</p>
    <template v-else>
      <p class="mb-2 font-medium" data-test="key-name">{{ key?.name ?? gatewayKeyId }}</p>
      <KeyUnavailableBadge class="mb-2" :gateway-key-id="gatewayKeyId" :department-id="departmentId" />
      <KeySettings v-if="key" class="mb-3 text-small" :gateway-key="key" />
      <KeyBudgetStatus class="mb-2" :gateway-key-id="gatewayKeyId" />
      <a v-if="link" :href="link" target="_blank" rel="noopener noreferrer" class="text-small text-accent-ink underline" data-test="key-link">{{ GATEWAY_KEY.openInGateway }}</a>
    </template>
  </UiCard>
</template>
