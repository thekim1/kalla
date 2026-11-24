<script setup lang="ts">
import type { GatewayKey } from '@/api/types'
import { formatDateTime, formatNumber } from '@/utils/format'
import { GATEWAY_KEY } from '@/utils/labels'

/**
 * A gateway key's settings, read-only (admin design §5.5.4 *Gateway key*, §5.5.2 the Overview's *Gateway key* card): team,
 * status, expiry, models, residencies, the personal-data and content-guard policies, the rate limits and the output cap.
 * They are kept in the gateway; Källa shows them and never edits them.
 */
defineProps<{ gatewayKey: GatewayKey }>()
const all = (values: string[]) => (values.length ? values.join(', ') : GATEWAY_KEY.all)
const limit = (n: number | null) => (n === null ? GATEWAY_KEY.unlimited : formatNumber(n))
</script>

<template>
  <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1" data-test="key-settings-list">
    <dt class="text-fg-2">{{ GATEWAY_KEY.team }}</dt><dd>{{ gatewayKey.teamName ?? gatewayKey.teamId }}</dd>
    <dt class="text-fg-2">{{ GATEWAY_KEY.status }}</dt><dd>{{ gatewayKey.status }}</dd>
    <dt class="text-fg-2">{{ GATEWAY_KEY.expires }}</dt><dd>{{ gatewayKey.expiresAt ? formatDateTime(gatewayKey.expiresAt) : GATEWAY_KEY.never }}</dd>
    <dt class="text-fg-2">{{ GATEWAY_KEY.models }}</dt><dd data-test="key-models">{{ all(gatewayKey.allowedModels) }}</dd>
    <dt class="text-fg-2">{{ GATEWAY_KEY.residencies }}</dt><dd>{{ all(gatewayKey.allowedResidencies) }}</dd>
    <dt class="text-fg-2">{{ GATEWAY_KEY.pii }}</dt><dd>{{ gatewayKey.piiPolicy }}</dd>
    <dt class="text-fg-2">{{ GATEWAY_KEY.contentGuard }}</dt><dd>{{ gatewayKey.contentGuardPolicy }}</dd>
    <dt class="text-fg-2">{{ GATEWAY_KEY.rateLimits }}</dt>
    <dd>{{ GATEWAY_KEY.rate(limit(gatewayKey.requestsPerMinute), limit(gatewayKey.tokensPerMinute), limit(gatewayKey.maxConcurrentRequests)) }}</dd>
    <dt class="text-fg-2">{{ GATEWAY_KEY.outputCap }}</dt>
    <dd>{{ gatewayKey.maxOutputTokens === null ? GATEWAY_KEY.unlimited : GATEWAY_KEY.tokens(formatNumber(gatewayKey.maxOutputTokens)) }}</dd>
  </dl>
</template>
