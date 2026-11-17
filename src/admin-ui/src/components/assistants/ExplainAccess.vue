<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { api } from '@/api'
import type { AccessExplanation, ClientSummary } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import SelectField from '@/components/ui/SelectField.vue'
import TextAreaField from '@/components/ui/TextAreaField.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiCard from '@/components/ui/UiCard.vue'
import { useAuthStore } from '@/stores/auth'
import { EXPLAIN } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'

// Explain access (admin design §4.5 row 21, identity design §4.9): why a client and a subject may or may not use this
// assistant, as the first failing policy step and its reason. Nothing is accessed and no security event is raised.
// An Open WebUI user is a subject to check only while the deployment runs Open WebUI; without it the check starts from a key.
const props = defineProps<{ assistantId: string; clients: ClientSummary[] }>()
const auth = useAuthStore()

const form = reactive({ clientId: '', kind: auth.openWebUiEnabled ? 'owui-user' : 'key', id: '', departments: '', groups: '' })
const result = ref<AccessExplanation | null>(null)
const error = ref<unknown>(null)
const idError = ref<string | null>(null)
const busy = ref(false)

const clientOptions = computed(() => props.clients.map((c) => ({ value: c.id, label: c.name })))
const kindOptions = Object.entries(EXPLAIN.kinds)
  .filter(([value]) => auth.openWebUiEnabled || value !== 'owui-user')
  .map(([value, label]) => ({ value, label }))
const lines = (text: string) => text.split(/[\n,]/).map((s) => s.trim()).filter(Boolean)

async function explain(): Promise<void> {
  idError.value = form.id.trim() ? null : EXPLAIN.idRequired
  if (idError.value || !form.clientId) return
  busy.value = true
  error.value = null
  result.value = null
  try {
    result.value = await api.access.explain({
      clientId: form.clientId,
      assistantId: props.assistantId,
      subject: {
        kind: form.kind, id: form.id.trim(),
        ...(form.kind === 'entra-user' ? { departments: lines(form.departments), groups: lines(form.groups) } : {}),
      },
    })
  } catch (e) {
    error.value = e
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <UiCard :title="EXPLAIN.title" data-test="explain">
    <p class="mb-4 text-small text-fg-2">{{ EXPLAIN.lead }}</p>
    <form class="grid gap-4" novalidate @submit.prevent="explain">
      <SelectField id="explain-client" v-model="form.clientId" :label="EXPLAIN.client" :options="clientOptions" :placeholder="EXPLAIN.chooseClient" />
      <SelectField id="explain-kind" v-model="form.kind" :label="EXPLAIN.kind" :options="kindOptions" />
      <TextField id="explain-id" v-model="form.id" :label="EXPLAIN.idLabels[form.kind] ?? EXPLAIN.id" :error="idError" mono />
      <template v-if="form.kind === 'entra-user'">
        <TextAreaField id="explain-departments" v-model="form.departments" :label="EXPLAIN.departments" :hint="EXPLAIN.listHint" :rows="2" mono />
        <TextAreaField id="explain-groups" v-model="form.groups" :label="EXPLAIN.groups" :hint="EXPLAIN.listHint" :rows="2" mono />
      </template>
      <div><UiButton type="submit" :loading="busy" :disabled="!form.clientId" data-test="run-explain">{{ EXPLAIN.run }}</UiButton></div>
    </form>
    <InlineError v-if="error" :message="problemMessage(error)" class="mt-4" />
    <div v-if="result" class="mt-4 rounded-tile border border-border p-4" data-test="explain-result" aria-live="polite">
      <UiBadge :tone="result.allowed ? 'ok' : 'warn'" :label="result.allowed ? EXPLAIN.allowed : EXPLAIN.refused" />
      <p v-if="result.allowed" class="mt-2">{{ EXPLAIN.allowedText(result.effectiveLevel, result.maxResidency) }}</p>
      <template v-else>
        <p class="mt-2">{{ EXPLAIN.reasons[result.reason] ?? EXPLAIN.unknownReason }}</p>
        <p class="mt-1 text-small text-fg-3">{{ EXPLAIN.step(result.step, result.reason, result.status) }}</p>
      </template>
    </div>
  </UiCard>
</template>
