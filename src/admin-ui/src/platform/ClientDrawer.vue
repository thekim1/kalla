<script setup lang="ts">
import { ref, watch } from 'vue'
import { RouterLink } from 'vue-router'
import { api } from '@/api'
import type { ApiKey, Client, KeyCreated, KeyRotationMode } from '@/api/types'
import ConfirmDialog from '@/components/ui/ConfirmDialog.vue'
import DataTable, { type Column } from '@/components/ui/DataTable.vue'
import DetailRow from '@/components/ui/DetailRow.vue'
import DetailSection from '@/components/ui/DetailSection.vue'
import InlineError from '@/components/ui/InlineError.vue'
import UiBadge from '@/components/ui/UiBadge.vue'
import UiButton from '@/components/ui/UiButton.vue'
import UiDrawer from '@/components/ui/UiDrawer.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import { useUiStore } from '@/stores/ui'
import { formatDate, formatDateTime } from '@/utils/format'
import { COMMON, PLATFORM, PLATFORM_CLIENTS, type Tone } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import ClientFields from './ClientFields.vue'
import CopyValue from './CopyValue.vue'
import KeyForm from './KeyForm.vue'
import SecretOnce from './SecretOnce.vue'
import { clientForm, clientRequest, newClientForm, RetentionDaysError, type ClientForm } from './clients'

// A client's drawer (admin design §5.9): its facts, the edit form of its kind (the kind is fixed), its keys with
// Rotate, Show key, Revoke and New key in place, and the assistants whose audience includes it.
const props = defineProps<{ open: boolean; clientId: string | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved'): void }>()

const L = PLATFORM_CLIENTS
const ui = useUiStore()
const client = ref<Client | null>(null)
const keys = ref<ApiKey[]>([])
const loading = ref(false)
const loadError = ref<unknown>(null)
const form = ref<ClientForm>(newClientForm(''))
const saving = ref(false)
const errors = useFormErrors({
  name: 'client-edit-name',
  classificationCeiling: 'client-edit-classificationCeiling',
  storesHistory: 'client-edit-storesHistory',
  clientRetentionDays: 'client-edit-clientRetentionDays',
  oAuthClientId: 'client-edit-oAuthClientId',
  portalUrl: 'client-edit-portalUrl',
  isEnabled: 'client-edit-isEnabled',
})

/** The key form in place of the table: a new key, or the key being edited. */
const keyEdit = ref<{ key: ApiKey | null } | null>(null)
const keyFormKey = ref(0)
const secret = ref<string | null>(null)
const keyError = ref<unknown>(null)
const rotating = ref<ApiKey | null>(null)
const rotateMode = ref<KeyRotationMode>('RevokeImmediately')
const revoking = ref<ApiKey | null>(null)

const keyColumns: Column[] = [
  { key: 'name', label: L.keys.columns.name },
  { key: 'prefix', label: L.keys.columns.prefix },
  { key: 'status', label: L.keys.columns.status },
  { key: 'expiresAt', label: L.keys.columns.expiresAt },
  { key: 'actions', label: L.keys.columns.actions, hideLabel: true },
]
const STATUS_TONE: Record<string, Tone> = { active: 'ok', 'in-grace-period': 'warn', expired: 'neutral', revoked: 'danger', disabled: 'neutral' }

async function loadKeys(id: string): Promise<void> {
  keys.value = await api.keys.list(id)
}

async function load(id: string): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    const [loaded] = await Promise.all([api.clients.get(id), loadKeys(id)])
    client.value = loaded
    form.value = clientForm(loaded)
  } catch (e) {
    loadError.value = e
  } finally {
    loading.value = false
  }
}

watch(() => [props.open, props.clientId] as const, ([open, id]) => {
  if (!open || !id) return
  client.value = null
  keys.value = []
  keyEdit.value = null
  secret.value = null
  keyError.value = null
  errors.clear()
  void load(id)
}, { immediate: true })

async function save(): Promise<void> {
  const current = client.value!
  let body
  try {
    body = clientRequest(current.kind, form.value, current)
  } catch (e) {
    if (!(e instanceof RetentionDaysError)) throw e
    errors.validate({ clientRetentionDays: PLATFORM.form.invalidNumber })
    return
  }
  if (!errors.validate({ name: !body.name && L.nameRequired })) return
  saving.value = true
  try {
    // The update's answer may leave out who uses the client; that has not changed.
    client.value = { ...current, ...(await api.clients.update(current.id, body)) }
    form.value = clientForm(client.value)
    ui.notify(L.saved)
    emit('saved')
  } catch (e) {
    errors.applyServerError(e)
  } finally {
    saving.value = false
  }
}

function editKey(key: ApiKey | null): void {
  secret.value = null
  keyError.value = null
  keyEdit.value = { key }
  keyFormKey.value++
}

async function keySaved(response: KeyCreated | ApiKey): Promise<void> {
  keyEdit.value = null
  // A new key carries its secret once (identity design §6.4).
  secret.value = 'secret' in response ? response.secret : null
  ui.notify(L.saved)
  await afterKeyChange()
}

async function afterKeyChange(): Promise<void> {
  emit('saved')
  await loadKeys(client.value!.id).catch((e: unknown) => (keyError.value = e))
}

function startRotate(key: ApiKey): void {
  rotateMode.value = 'RevokeImmediately'
  rotating.value = key
}

async function rotate(): Promise<void> {
  secret.value = (await api.keys.rotate(rotating.value!.id, rotateMode.value)).secret
  await afterKeyChange()
}

async function reveal(key: ApiKey): Promise<void> {
  keyError.value = null
  try {
    secret.value = (await api.keys.reveal(key.id)).secret
  } catch (e) {
    keyError.value = e
  }
}

async function revoke(): Promise<void> {
  await api.keys.revoke(revoking.value!.id)
  secret.value = null
  await afterKeyChange()
}
</script>

<template>
  <UiDrawer :open="open" :title="client?.name ?? COMMON.loading" wide @update:open="emit('update:open', $event)">
    <p v-if="loading">{{ COMMON.loading }}</p>
    <InlineError v-if="loadError" :message="problemMessage(loadError)" class="mb-4" />
    <template v-if="client">
      <DetailSection :title="L.facts.title" data-test="client-facts">
        <DetailRow :label="PLATFORM.form.id"><CopyValue :value="client.id" /></DetailRow>
        <DetailRow :label="L.facts.kind">{{ L.kinds[client.kind]?.title ?? client.kind }}</DetailRow>
        <DetailRow :label="L.facts.updated">{{ formatDateTime(client.updatedAt) }}<template v-if="client.updatedBy"> · {{ client.updatedBy }}</template></DetailRow>
      </DetailSection>

      <section class="mb-8 grid gap-4" aria-labelledby="client-edit-title">
        <h3 id="client-edit-title" class="text-caption text-fg-3">{{ L.settings }}</h3>
        <form id="client-edit" class="grid gap-5" novalidate data-test="client-edit" @submit.prevent="save">
          <ClientFields v-model="form" :kind="client.kind" id-prefix="client-edit" :errors="errors.errors" />
          <InlineError v-if="errors.formError.value" :message="errors.formError.value" />
          <div><UiButton variant="primary" type="submit" :loading="saving" data-test="save-client">{{ L.save }}</UiButton></div>
        </form>
      </section>

      <section class="mb-8 grid gap-3" aria-labelledby="client-keys-title" data-test="client-keys">
        <div class="flex items-center justify-between gap-4">
          <h3 id="client-keys-title" class="text-caption text-fg-3">{{ L.keys.title }}</h3>
          <UiButton v-if="!keyEdit" size="sm" icon="add" data-test="new-key" @click="editKey(null)">{{ L.keys.newKey }}</UiButton>
        </div>
        <SecretOnce v-if="secret" :secret="secret" />
        <InlineError v-if="keyError" :message="problemMessage(keyError)" />
        <div v-if="keyEdit" class="grid gap-3">
          <h4 class="font-medium">{{ keyEdit.key ? L.keys.editTitle(keyEdit.key.name) : L.keys.newTitle }}</h4>
          <KeyForm :key="keyFormKey" form-id="client-key" :api-key="keyEdit.key" :client-id="client.id" @saved="keySaved" @cancel="keyEdit = null" />
        </div>
        <DataTable
          v-else :columns="keyColumns" :rows="keys" :row-key="(k) => k.id" :caption="L.keys.title" :empty-text="L.keys.empty" clickable
          @row-click="editKey"
        >
          <template #cell-name="{ row }"><span class="font-medium">{{ row.name }}</span></template>
          <template #cell-prefix="{ row }"><span class="font-mono text-small">{{ row.prefix }}</span></template>
          <template #cell-status="{ row }"><UiBadge :tone="STATUS_TONE[row.status] ?? 'neutral'" :label="L.keyStatus[row.status] ?? row.status" /></template>
          <template #cell-expiresAt="{ row }">{{ row.expiresAt ? formatDate(row.expiresAt) : L.keys.never }}</template>
          <template #cell-actions="{ row }">
            <span v-if="row.status !== 'revoked'" class="flex flex-wrap justify-end gap-1">
              <UiButton v-if="row.status === 'active' && !row.rotatedToKeyId" size="sm" :data-test="`rotate-${row.id}`" @click.stop="startRotate(row)">
                {{ PLATFORM.actions.rotate }}
              </UiButton>
              <UiButton size="sm" :data-test="`reveal-${row.id}`" @click.stop="reveal(row)">{{ PLATFORM.actions.reveal }}</UiButton>
              <UiButton size="sm" variant="danger" :data-test="`revoke-${row.id}`" @click.stop="revoking = row">{{ PLATFORM.actions.revoke }}</UiButton>
            </span>
          </template>
        </DataTable>
      </section>

      <section class="grid gap-2" aria-labelledby="client-used-by-title" data-test="client-used-by">
        <h3 id="client-used-by-title" class="text-caption text-fg-3">{{ L.usedBy.title }}</h3>
        <ul v-if="client.assistants.length" class="grid gap-1">
          <li v-for="a in client.assistants" :key="a.id" class="flex items-center gap-2">
            <RouterLink :to="{ name: 'assistant', params: { id: a.id } }" class="font-medium text-accent-ink underline">{{ a.name }}</RouterLink>
            <UiBadge :tone="a.published ? 'ok' : 'neutral'" :label="a.published ? L.usedBy.published : L.usedBy.draft" />
          </li>
        </ul>
        <p v-else class="text-fg-2">{{ L.usedBy.empty }}</p>
      </section>
    </template>
  </UiDrawer>
  <ConfirmDialog
    :open="!!rotating" :title="L.rotate.title" :confirm-label="PLATFORM.actions.rotate" :action="rotate" @update:open="!$event && (rotating = null)"
  >
    <p>{{ L.rotate.consequence }}</p>
    <fieldset class="mt-3 grid gap-2">
      <legend class="mb-1 font-medium">{{ L.rotate.mode }}</legend>
      <label class="flex items-center gap-2"><input v-model="rotateMode" type="radio" name="rotate-mode" value="RevokeImmediately" /> {{ L.rotate.immediately }}</label>
      <label class="flex items-center gap-2"><input v-model="rotateMode" type="radio" name="rotate-mode" value="Grace24Hours" /> {{ L.rotate.grace }}</label>
    </fieldset>
  </ConfirmDialog>
  <ConfirmDialog
    :open="!!revoking" :title="PLATFORM.actions.revoke" :consequence="PLATFORM.actions.revokeConfirm" :confirm-label="PLATFORM.actions.revoke" danger
    :action="revoke" @update:open="!$event && (revoking = null)"
  />
</template>
