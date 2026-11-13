<script setup lang="ts">
import { ref, watch } from 'vue'
import { api } from '@/api'
import type { Client, ClientCreated } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import InlineError from '@/components/ui/InlineError.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { COMMON, PLATFORM, PLATFORM_CLIENTS } from '@/utils/labels'
import ClientFields from './ClientFields.vue'
import CopyValue from './CopyValue.vue'
import SecretOnce from './SecretOnce.vue'
import { AVAILABLE_KINDS, clientRequest, fieldsFor, LATER_KINDS, newClientForm, RetentionDaysError, stackLines, type ClientForm } from './clients'

// Add client (admin design §5.9): the kind, that kind's fields, and a first key made in the same transaction. The result
// shows the secret and the client's id once, and for Open WebUI the two stack lines.
const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'created', client: Client): void }>()

const L = PLATFORM_CLIENTS
const STEPS = [L.steps.kind, L.steps.fields, L.steps.firstKey]
const KINDS = [...AVAILABLE_KINDS, ...LATER_KINDS]
const step = ref(1)
const kind = ref('')
const form = ref<ClientForm>(newClientForm(''))
const createKey = ref(true)
const keyName = ref('')
const saving = ref(false)
const result = ref<ClientCreated | null>(null)
const errors = useFormErrors({
  kind: 'client-new-kind-open-webui',
  name: 'client-new-name',
  classificationCeiling: 'client-new-classificationCeiling',
  storesHistory: 'client-new-storesHistory',
  clientRetentionDays: 'client-new-clientRetentionDays',
  oAuthClientId: 'client-new-oAuthClientId',
  portalUrl: 'client-new-portalUrl',
  isEnabled: 'client-new-isEnabled',
  'firstKey.name': 'client-new-key-name',
})
// The key name follows the client's name until it is edited.
let defaultKeyName = ''

watch(() => props.open, (open) => {
  if (!open) return
  step.value = 1
  kind.value = ''
  form.value = newClientForm('')
  createKey.value = true
  keyName.value = ''
  defaultKeyName = ''
  result.value = null
  errors.clear()
})

function chooseKind(value: string): void {
  if (value === kind.value) return
  // A new kind starts from its own defaults; the name stays.
  form.value = { ...newClientForm(value), name: form.value.name }
  kind.value = value
}

function request() {
  try {
    return clientRequest(kind.value, form.value, null)
  } catch (e) {
    if (!(e instanceof RetentionDaysError)) throw e
    return null
  }
}

function next(): void {
  if (step.value === 1) {
    if (!errors.validate({ kind: !kind.value && L.kindRequired })) return
  } else if (step.value === 2) {
    const ok = errors.validate({ name: !form.value.name.trim() && L.nameRequired, clientRetentionDays: !request() && PLATFORM.form.invalidNumber })
    if (!ok) return
    if (keyName.value === defaultKeyName) keyName.value = L.firstKey.defaultName(form.value.name.trim())
    defaultKeyName = keyName.value
  }
  step.value++
}

function back(): void {
  errors.clear()
  step.value--
}

async function create(): Promise<void> {
  const body = request()
  if (!body || !errors.validate({ 'firstKey.name': createKey.value && !keyName.value.trim() && L.nameRequired })) return
  saving.value = true
  try {
    result.value = await api.clients.create({ ...body, firstKey: createKey.value ? { name: keyName.value.trim() } : null })
    emit('created', result.value.client)
  } catch (e) {
    errors.applyServerError(e)
    // A refused field of step 2 is shown where it is edited.
    if (fieldsFor(kind.value).some((f) => errors.errors[f])) step.value = 2
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="L.add" wide @update:open="emit('update:open', $event)">
    <div v-if="result" class="grid gap-6" data-test="client-created">
      <SecretOnce v-if="result.key" :secret="result.key.secret" />
      <p v-else class="text-fg-2">{{ L.result.noKey }}</p>
      <div class="grid gap-2">
        <h3 class="font-medium">{{ L.result.clientId }}</h3>
        <CopyValue :value="result.client.id" data-test="client-id" />
      </div>
      <div v-if="result.key && result.client.kind === 'open-webui'" class="grid gap-2" data-test="stack-lines">
        <h3 class="font-medium">{{ L.result.stackLines }}</h3>
        <CopyValue :value="stackLines(result.key.secret, result.client.id)" :copy-label="L.result.copyLines" block />
        <p class="text-small text-fg-2">{{ L.result.stackSentence }}</p>
      </div>
    </div>
    <form v-else id="client-new" class="grid gap-5" novalidate @submit.prevent="step < 3 ? next() : create()">
      <p class="text-caption text-fg-3" data-test="step">{{ L.step(step, STEPS.length, STEPS[step - 1]!) }}</p>
      <fieldset v-if="step === 1" class="grid gap-2" data-test="kinds">
        <legend class="sr-only">{{ L.steps.kind }}</legend>
        <label
          v-for="k in KINDS" :key="k" class="flex gap-3 rounded-card border border-border p-3"
          :class="LATER_KINDS.includes(k) ? 'cursor-not-allowed opacity-70' : 'cursor-pointer'"
        >
          <input
            :id="`client-new-kind-${k}`" type="radio" name="client-kind" :value="k" class="mt-1" :checked="kind === k" :disabled="LATER_KINDS.includes(k)"
            @change="chooseKind(k)"
          />
          <span class="grid gap-0.5">
            <span class="flex items-center gap-2 font-medium">{{ L.kinds[k]?.title ?? k }}<UiBadge v-if="LATER_KINDS.includes(k)" :label="L.later" :dot="false" /></span>
            <span class="text-small text-fg-2">{{ L.kinds[k]?.description }}</span>
          </span>
        </label>
        <p v-if="errors.errors.kind" class="text-small text-danger">{{ errors.errors.kind }}</p>
      </fieldset>
      <ClientFields v-else-if="step === 2" v-model="form" :kind="kind" id-prefix="client-new" :errors="errors.errors" />
      <div v-else class="grid gap-4">
        <CheckField v-model="createKey" :label="L.firstKey.create" :description="L.firstKey.hint" data-test="create-key" />
        <TextField v-if="createKey" id="client-new-key-name" v-model="keyName" :label="L.firstKey.name" :error="errors.errors['firstKey.name']" autocomplete="off" />
      </div>
      <InlineError v-if="errors.formError.value" :message="errors.formError.value" />
    </form>
    <template #footer>
      <UiButton v-if="result" @click="emit('update:open', false)">{{ COMMON.close }}</UiButton>
      <template v-else>
        <UiButton v-if="step < 3" variant="primary" type="submit" form="client-new" data-test="next">{{ L.next }}</UiButton>
        <UiButton v-else variant="primary" type="submit" form="client-new" :loading="saving" data-test="create">{{ L.create }}</UiButton>
        <UiButton v-if="step > 1" data-test="back" @click="back">{{ L.back }}</UiButton>
        <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
      </template>
    </template>
  </UiDrawer>
</template>
