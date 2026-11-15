<script setup lang="ts">
import { computed, ref, watch } from 'vue'
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
import { formatDate, formatDateTime, formatRelative } from '@/utils/format'
import { COMMON, PLATFORM, PLATFORM_CLIENTS } from '@/utils/labels'
import { problemMessage } from '@/utils/problem'
import AssistantPicker from './AssistantPicker.vue'
import { useClientChoices } from './clientChoices'
import ClientFields from './ClientFields.vue'
import CopyValue from './CopyValue.vue'
import HowToConnect from './HowToConnect.vue'
import KeyForm from './KeyForm.vue'
import SecretOnce from './SecretOnce.vue'
import { clientForm, clientRequest, KEY_STATUS_TONE, newClientForm, RetentionDaysError, type ClientForm } from './clients'

// An app's drawer (admin design §5.9, §5.9.1): its facts, the edit form of its kind (the kind is fixed), the assistants
// it offers (a checklist with a save of its own), its keys with Rotate, Show key, Revoke and New key in place and each
// key's last use, and "How to connect" with the key that is on screen.
// `keyId` opens that key's form once the drawer has loaded (a row of All keys).
const props = defineProps<{ open: boolean; clientId: string | null; keyId?: string | null }>()
const emit = defineEmits<{ (e: 'update:open', value: boolean): void; (e: 'saved'): void }>()

const L = PLATFORM_CLIENTS
const ui = useUiStore()
const client = ref<Client | null>(null)
const keys = ref<ApiKey[]>([])
const loading = ref(false)
const loadError = ref<unknown>(null)
const form = ref<ClientForm>(newClientForm(''))
const saving = ref(false)
/** The pickers' rows (this one's and the key form's), the address of "How to connect" and the ticked assistants. */
const { assistants, departments, collections, apiBaseUrl, load: loadChoices } = useClientChoices({ collections: true })
const assistantIds = ref<string[]>([])
const savingAssistants = ref(false)
const assistantErrors = useFormErrors({ assistantIds: 'client-edit-assistantIds' })
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
  { key: 'status', label: L.keys.columns.status },
  { key: 'expiresAt', label: L.keys.columns.expiresAt },
  { key: 'lastUsed', label: L.keys.columns.lastUsed },
  { key: 'actions', label: L.keys.columns.actions, hideLabel: true },
]

async function loadKeys(id: string): Promise<void> {
  keys.value = await api.keys.list(id)
}

async function load(id: string): Promise<void> {
  loading.value = true
  loadError.value = null
  try {
    const [loaded] = await Promise.all([api.clients.get(id), loadKeys(id), loadChoices()])
    client.value = loaded
    form.value = clientForm(loaded)
    assistantIds.value = loaded.assistants.map((a) => a.id)
    const opened = props.keyId ? keys.value.find((k) => k.id === props.keyId) : undefined
    if (opened) editKey(opened)
  } catch (e) {
    loadError.value = e
  } finally {
    loading.value = false
  }
}

/** Active assistants, and any the app offers already (an archived one can still be unticked). */
const pickable = computed(() => assistants.value.filter((a) => a.state === 'active' || assistantIds.value.includes(a.id)))

async function saveAssistants(): Promise<void> {
  const current = client.value!
  savingAssistants.value = true
  try {
    client.value = await api.clients.setAssistants(current.id, { assistantIds: assistantIds.value, version: current.version })
    assistantIds.value = client.value.assistants.map((a) => a.id)
    assistantErrors.clear()
    ui.notify(L.assistants.saved)
    emit('saved')
  } catch (e) {
    assistantErrors.applyServerError(e)
  } finally {
    savingAssistants.value = false
  }
}

watch(() => [props.open, props.clientId, props.keyId] as const, ([open, id]) => {
  // A shown-once secret does not stay in memory after the drawer closes.
  if (!open) secret.value = null
  if (!open || !id) return
  client.value = null
  keys.value = []
  keyEdit.value = null
  secret.value = null
  keyError.value = null
  errors.clear()
  assistantErrors.clear()
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
    client.value = await api.clients.update(current.id, body)
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
        <DetailRow :label="L.facts.kind">
          {{ L.kinds[client.kind]?.title ?? client.kind }}
          <UiBadge v-if="client.isManagedByDeployment" :label="L.managed" :dot="false" class="ml-2" data-test="managed" />
        </DetailRow>
        <DetailRow :label="L.facts.updated">{{ formatDateTime(client.updatedAt) }}</DetailRow>
      </DetailSection>

      <section class="mb-8 grid gap-4" aria-labelledby="client-edit-title">
        <h3 id="client-edit-title" class="text-caption text-fg-3">{{ L.settings }}</h3>
        <form id="client-edit" class="grid gap-5" novalidate data-test="client-edit" @submit.prevent="save">
          <ClientFields v-model="form" :kind="client.kind" id-prefix="client-edit" :errors="errors.errors" />
          <InlineError v-if="errors.formError.value" :message="errors.formError.value" />
          <div><UiButton variant="primary" type="submit" :loading="saving" data-test="save-client">{{ L.save }}</UiButton></div>
        </form>
      </section>

      <section class="mb-8 grid gap-3" data-test="client-assistants">
        <AssistantPicker
          v-model="assistantIds" id-prefix="client-edit" :assistants="pickable" :departments="departments" :ceiling="client.classificationCeiling"
          :error="assistantErrors.errors.assistantIds"
        />
        <InlineError v-if="assistantErrors.formError.value" :message="assistantErrors.formError.value" />
        <div v-if="pickable.length"><UiButton :loading="savingAssistants" data-test="save-assistants" @click="saveAssistants">{{ L.assistants.save }}</UiButton></div>
      </section>

      <section class="mb-8 grid gap-3" aria-labelledby="client-keys-title" data-test="client-keys">
        <p v-if="client.isManagedByDeployment" class="text-small text-fg-2" data-test="managed-note">{{ L.managedNote }}</p>
        <div class="flex items-center justify-between gap-4">
          <h3 id="client-keys-title" class="text-caption text-fg-3">{{ L.keys.title }}</h3>
          <UiButton v-if="!keyEdit && !client.isManagedByDeployment" size="sm" icon="add" data-test="new-key" @click="editKey(null)">{{ L.keys.newKey }}</UiButton>
        </div>
        <SecretOnce v-if="secret" :secret="secret" />
        <InlineError v-if="keyError" :message="problemMessage(keyError)" />
        <div v-if="keyEdit" class="grid gap-3">
          <h4 class="font-medium">{{ keyEdit.key ? L.keys.editTitle(keyEdit.key.name) : L.keys.newTitle }}</h4>
          <KeyForm :key="keyFormKey" form-id="client-key" :api-key="keyEdit.key" :client-id="client.id"
            :audience-ids="client.assistants.map((a) => a.id)" :assistants="assistants" :departments="departments" :collections="collections" @saved="keySaved" @cancel="keyEdit = null"
          />
        </div>
        <DataTable
          v-else :columns="keyColumns" :rows="keys" :row-key="(k) => k.id" :caption="L.keys.title" :empty-text="L.keys.empty" clickable
          @row-click="editKey"
        >
          <!-- The prefix under the name: the drawer is too narrow for a column of its own beside the last use. -->
          <template #cell-name="{ row }">
            <span class="block font-medium">{{ row.name }}</span>
            <span class="block font-mono text-caption whitespace-nowrap text-fg-3">{{ row.prefix }}</span>
          </template>
          <template #cell-status="{ row }"><UiBadge :tone="KEY_STATUS_TONE[row.status] ?? 'neutral'" :label="L.keyStatus[row.status] ?? row.status" /></template>
          <template #cell-expiresAt="{ row }">{{ row.expiresAt ? formatDate(row.expiresAt) : L.keys.never }}</template>
          <template #cell-lastUsed="{ row }">
            <span :title="row.lastUsedAt ? formatDateTime(row.lastUsedAt) : undefined">{{ row.lastUsedAt ? formatRelative(row.lastUsedAt) : L.neverUsed }}</span>
          </template>
          <template #cell-actions="{ row }">
            <!-- The deployment's key is replaced by deploying a new one (identity design §5.2): no actions here. -->
            <span v-if="row.status !== 'revoked' && !client.isManagedByDeployment" class="flex flex-wrap justify-end gap-1">
              <UiButton v-if="row.status === 'active' && !row.rotatedToKeyId" size="sm" :data-test="`rotate-${row.id}`" @click.stop="startRotate(row)">
                {{ PLATFORM.actions.rotate }}
              </UiButton>
              <UiButton size="sm" :data-test="`reveal-${row.id}`" @click.stop="reveal(row)">{{ PLATFORM.actions.reveal }}</UiButton>
              <UiButton size="sm" variant="danger" :data-test="`revoke-${row.id}`" @click.stop="revoking = row">{{ PLATFORM.actions.revoke }}</UiButton>
            </span>
          </template>
        </DataTable>
      </section>

      <section class="grid gap-3" aria-labelledby="client-connect-title" data-test="client-connect">
        <h3 id="client-connect-title" class="text-caption text-fg-3">{{ L.connect.title }}</h3>
        <HowToConnect :kind="client.kind" :client-id="client.id" :api-base-url="apiBaseUrl" :secret="secret" :assistants="client.assistants" />
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
