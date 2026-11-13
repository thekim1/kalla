<script setup lang="ts">
import { ref, watch } from 'vue'
import { api } from '@/api'
import type { KeyBudget } from '@/api/types'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiMeter from '@/components/ui/UiMeter.vue'
import { formatDate, formatNumber, formatPercent, formatUsdCost } from '@/utils/format'
import { GATEWAY_KEY } from '@/utils/labels'

// The key header's budget status (admin design §5.5.4, §5.5.2; B-46): per budget that applies to the object's gateway key its
// scope, spent of limit with the percentage, the period's end, and *Used up* when exhausted, from the gateway's Integration
// API (B-45). Nothing is shown without a key or without the read-back (`null`).
const props = defineProps<{ gatewayKeyId: string | null | undefined }>()

const budgets = ref<KeyBudget[] | null>(null)

watch(
  () => props.gatewayKeyId,
  async (key) => {
    budgets.value = null
    if (!key) return
    budgets.value = await api.gateway.keyBudget(key).catch(() => null)
  },
  { immediate: true },
)

const amount = (b: KeyBudget, value: number) => (b.unit === 'usd' ? formatUsdCost(value) : GATEWAY_KEY.tokens(formatNumber(value)))
</script>

<template>
  <section v-if="budgets && budgets.length" class="grid gap-2" data-test="key-budgets" :aria-label="GATEWAY_KEY.budgets">
    <h3 class="text-small font-medium text-fg-2">{{ GATEWAY_KEY.budgets }}</h3>
    <div v-for="(b, i) in budgets" :key="i" class="grid gap-1" :data-test="`key-budget-${b.scope}`">
      <p class="flex flex-wrap items-center gap-2 text-small">
        <span class="font-medium">{{ GATEWAY_KEY.budgetScopes[b.scope] ?? b.scope }}</span>
        <span class="tabular">{{ GATEWAY_KEY.spentOf(amount(b, b.spent), amount(b, b.limit), formatPercent(b.percentUsed)) }}</span>
        <span class="text-fg-3">{{ GATEWAY_KEY.until(formatDate(b.periodEnd)) }}</span>
        <UiBadge v-if="b.exhausted" tone="danger" :label="GATEWAY_KEY.usedUp" data-test="budget-used-up" />
      </p>
      <UiMeter :value="b.percentUsed" :label="GATEWAY_KEY.budgetScopes[b.scope] ?? b.scope" thin />
    </div>
  </section>
</template>
