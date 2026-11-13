<script setup lang="ts">
import { ref, watch } from 'vue'
import { api } from '@/api'
import type { AssistantSummary, Client, ClientCreated, Department } from '@/api/types'
import CheckField from '@/components/ui/CheckField.vue'
import InlineError from '@/components/ui/InlineError.vue'
import TextField from '@/components/ui/TextField.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { COMMON, PLATFORM, PLATFORM_CLIENTS } from '@/utils/labels'
import AssistantPicker from './AssistantPicker.vue'
import ClientFields from './ClientFields.vue'
import HowToConnect from './HowToConnect.vue'
import { assistantAllowed, AVAILABLE_KINDS, clientRequest, fieldsFor, LATER_KINDS, newClientForm, RetentionDaysError, type ClientForm } from './clients'

// Add app (admin design §5.9, §5.9.1): the kind, that kind's fields, the assistants it offers and a first key, all made in
// one transaction. The result is "How to connect". `assistantId` ticks one assistant (opened from its Who can use it tab).
const props = defineProps<{ open: boolean; assistantId?: string | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'created', client: Client): void }>()

const L = PLATFORM_CLIENTS
const STEPS = [L.steps.kind, L.steps.fields, L.steps.assistants, L.steps.firstKey]
const LAST = STEPS.length
const KINDS = [...AVAILABLE_KINDS, ...LATER_KINDS]
const step = ref(1)
const kind = ref('')
const form = ref<ClientForm>(newClientForm(''))
const assistantIds = ref<string[]>([])
const assistants = ref<AssistantSummary[]>([])
const departments = ref<Department[]>([])
const apiBaseUrl = ref<string | null>(null)
const loadingChoices = ref(false)
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
  assistantIds: 'client-new-assistantIds',
  'firstKey.name': 'client-new-key-name',
})
// The key name follows the client's name until it is edited.
let defaultKeyName = ''

watch(() => props.open, (open) => {
  if (!open) return
  step.value = 1
  kind.value = ''
  form.value = newClientForm('')
  assistantIds.value = props.assistantId ? [props.assistantId] : []
  createKey.value = true
  keyName.value = ''
  defaultKeyName = ''
  result.value = null
  errors.clear()
  void loadChoices()
})

/** The picker's rows and the address of "How to connect"; a failed read leaves the step empty, not the drawer broken. */
async function loadChoices(): Promise<void> {
  loadingChoices.value = true
  ;[assistants.value, departments.value, apiBaseUrl.value] = await Promise.all([
    api.assistants.list({ pageSize: 200 }).then((p) => p.items.filter((a) => a.state === 'active')).catch(() => []),
    api.organisation.departments().catch(() => []),
    api.clients.connection().then((c) => c.apiBaseUrl).catch(() => null),
  ])
  loadingChoices.value = false
}

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
    // An assistant above the information class just chosen cannot stay ticked (V7).
    const level = new Map(assistants.value.map((a) => [a.id, a.classification]))
    assistantIds.value = assistantIds.value.filter((id) => !level.has(id) || assistantAllowed(level.get(id)!, form.value.classificationCeiling))
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
    result.value = await api.clients.create({ ...body, firstKey: createKey.value ? { name: keyName.value.trim() } : null, assistantIds: assistantIds.value })
    emit('created', result.value.client)
  } catch (e) {
    errors.applyServerError(e)
    // A refused field is shown on the step where it is edited.
    if (fieldsFor(kind.value).some((f) => errors.errors[f])) step.value = 2
    else if (errors.errors.assistantIds) step.value = 3
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <UiDrawer :open="open" :title="L.add" wide @update:open="emit('update:open', $event)">
    <div v-if="result" class="grid gap-4" data-test="client-created">
      <h3 class="text-caption text-fg-3">{{ L.connect.title }}</h3>
      <HowToConnect
        :kind="result.client.kind" :client-id="result.client.id" :api-base-url="apiBaseUrl" :secret="result.key?.secret ?? null"
        :assistants="result.client.assistants" created
      />
    </div>
    <form v-else id="client-new" class="grid gap-5" novalidate @submit.prevent="step < LAST ? next() : create()">
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
      <AssistantPicker
        v-else-if="step === 3" v-model="assistantIds" id-prefix="client-new" :assistants="assistants" :departments="departments"
        :ceiling="form.classificationCeiling" :error="errors.errors.assistantIds" :loading="loadingChoices"
      />
      <div v-else class="grid gap-4">
        <CheckField v-model="createKey" :label="L.firstKey.create" :description="L.firstKey.hint" data-test="create-key" />
        <TextField v-if="createKey" id="client-new-key-name" v-model="keyName" :label="L.firstKey.name" :error="errors.errors['firstKey.name']" autocomplete="off" />
      </div>
      <InlineError v-if="errors.formError.value" :message="errors.formError.value" />
    </form>
    <template #footer>
      <UiButton v-if="result" @click="emit('update:open', false)">{{ COMMON.close }}</UiButton>
      <template v-else>
        <UiButton v-if="step < LAST" variant="primary" type="submit" form="client-new" data-test="next">{{ L.next }}</UiButton>
        <UiButton v-else variant="primary" type="submit" form="client-new" :loading="saving" data-test="create">{{ L.create }}</UiButton>
        <UiButton v-if="step > 1" data-test="back" @click="back">{{ L.back }}</UiButton>
        <UiButton variant="quiet" @click="emit('update:open', false)">{{ COMMON.cancel }}</UiButton>
      </template>
    </template>
  </UiDrawer>
</template>
