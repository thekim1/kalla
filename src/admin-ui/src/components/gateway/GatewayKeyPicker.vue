<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { api } from '@/api'
import type { GatewayKey, GatewayKeyList } from '@/api/types'
import SelectField from '@/components/ui/SelectField.vue'
import { gatewayHref } from '@/utils/gateway'
import KeySettings from './KeySettings.vue'
import { GATEWAY_KEY } from '@/utils/labels'

// The gateway key picker (admin design §4.5, §5.5.2, §5.5.4; B-36): the department's assigned keys from the mirror. A key
// that cannot be chosen (not active, owner inactive, no residency in common with the platform's key) is listed, not
// selectable, with its reason; the chosen key's settings are shown read-only, with a link to it in the gateway. V39's
// other checks (residency, models) run when the object is saved.
const props = defineProps<{ id: string; departmentId: string; modelValue: string | null; hint: string; error?: string | null }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: string | null): void }>()

const list = ref<GatewayKeyList | null>(null)
const failed = ref(false)

watch(
  () => props.departmentId,
  async (department) => {
    failed.value = false
    try {
      list.value = await api.gateway.keys(department)
    } catch {
      list.value = null
      failed.value = true
    }
  },
  { immediate: true },
)

const keys = computed<GatewayKey[]>(() => list.value?.keys ?? [])
const chosen = computed(() => keys.value.find((k) => k.id === props.modelValue) ?? null)
// A saved key the mirror no longer lists (revoked): kept as the value, marked, until another is chosen.
const gone = computed(() => props.modelValue !== null && list.value !== null && chosen.value === null)
const options = computed(() => [
  { value: '', label: GATEWAY_KEY.none },
  ...(gone.value ? [{ value: props.modelValue!, label: `${props.modelValue} — ${GATEWAY_KEY.gone}`, disabled: true }] : []),
  ...keys.value.map((k) => ({
    value: k.id,
    label: k.selectable ? k.name : `${k.name} — ${GATEWAY_KEY.notSelectable(k.reason ?? k.status)}`,
    // The saved key stays selectable in the list so the form shows it, even when it can no longer be chosen anew.
    disabled: !k.selectable && k.id !== props.modelValue,
  })),
])
// The gateway's admin UI has a Keys page and no address per key (v0.10.0 router): the link opens it.
const link = computed(() => (chosen.value ? gatewayHref(list.value?.adminUrl, '/keys') : null))
</script>

<template>
  <div class="grid gap-3">
    <SelectField
      :id="id"
      :label="GATEWAY_KEY.label"
      :model-value="modelValue ?? ''"
      :options="options"
      :hint="hint"
      :error="error"
      :disabled="list === null && !failed"
      @update:model-value="emit('update:modelValue', $event || null)"
    />
    <p v-if="failed" class="text-small text-danger" role="alert" data-test="key-load-failed">{{ GATEWAY_KEY.loadFailed }}</p>
    <p v-else-if="list && keys.length === 0" class="text-small text-fg-2" data-test="no-keys">{{ GATEWAY_KEY.noKeys }}</p>
    <p v-if="modelValue === null && list" class="text-small text-warn" data-test="no-key-warning">{{ GATEWAY_KEY.noneWarning }}</p>
    <p v-if="gone" class="text-small text-danger" data-test="key-gone">{{ GATEWAY_KEY.gone }}</p>
    <div v-if="chosen" class="rounded-card border border-border p-3 text-small" data-test="key-settings">
      <p class="mb-2 font-medium">{{ GATEWAY_KEY.settings }}</p>
      <p v-if="chosen.hasSystemPrompt" class="mb-2 text-warn" data-test="key-system-prompt">{{ GATEWAY_KEY.systemPrompt }}</p>
      <p v-if="!chosen.selectable" class="mb-2 text-danger" data-test="key-unusable">{{ GATEWAY_KEY.notSelectable(chosen.reason ?? chosen.status) }}</p>
      <KeySettings :gateway-key="chosen" />
      <a v-if="link" :href="link" target="_blank" rel="noopener noreferrer" class="mt-2 inline-block underline" data-test="key-link">{{ GATEWAY_KEY.openInGateway }}</a>
    </div>
  </div>
</template>
