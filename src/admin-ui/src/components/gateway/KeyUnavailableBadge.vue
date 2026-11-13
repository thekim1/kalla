<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { GatewayKey, KeyBudget } from '@/api/types'
import UiBadge from '@/components/ui/UiBadge.vue'
import { GATEWAY_KEY } from '@/utils/labels'
import { KEY_UNAVAILABLE_MESSAGES } from '@/utils/messages'

// The [Unavailable] badge of admin design §5.5.2 and §5.5.4 (B-44): an object whose gateway key is missing from the mirror,
// not active, its owner inactive or not usable shows it with the reason; chat answers such an assistant as unavailable
// (chat §3.6) and the worker pauses such a knowledge base. Derived from the mirror (`GET /api/gateway/keys`); a spent budget
// joins it with the Integration API's budgets (B-45). Nothing is shown without a key, or while the list cannot be read.
const props = defineProps<{ gatewayKeyId: string | null | undefined; departmentId: string }>()

const keys = ref<GatewayKey[] | null>(null)
// B-46: a spent budget (the Integration API's `exhausted`, B-45) is a reason too; null without the read-back.
const budgets = ref<KeyBudget[] | null>(null)

watch(
  () => [props.gatewayKeyId, props.departmentId] as const,
  async ([key, department]) => {
    keys.value = null
    budgets.value = null
    if (!key) return
    const [list, spent] = await Promise.all([
      api.gateway.keys(department).then((l) => l.keys).catch(() => null),
      api.gateway.keyBudget(key).catch(() => null),
    ])
    keys.value = list
    budgets.value = spent
  },
  { immediate: true },
)

/** The reason's code: `missing`, `disabled`, `expired`, `revoked`, `not_active`, `owner_inactive`, `unusable`; null when usable. */
const reason = computed<string | null>(() => {
  if (!props.gatewayKeyId || keys.value === null) return null
  const key = keys.value.find((k) => k.id === props.gatewayKeyId)
  if (!key) return 'missing'
  if (key.status !== 'Active') return ({ Disabled: 'disabled', Expired: 'expired', Revoked: 'revoked' } as Record<string, string>)[key.status] ?? 'not_active'
  if (!key.ownerActive) return 'owner_inactive'
  if (!key.usable) return 'unusable'
  const spent = budgets.value?.find((b) => b.exhausted)
  return spent ? `budget_${spent.scope}` : null
})
</script>

<template>
  <span v-if="reason" class="inline-flex items-center gap-2" data-test="key-unavailable">
    <UiBadge tone="danger" :label="GATEWAY_KEY.unavailable" />
    <span class="text-small text-danger" data-test="key-unavailable-reason">{{ KEY_UNAVAILABLE_MESSAGES[reason] }}</span>
  </span>
</template>
