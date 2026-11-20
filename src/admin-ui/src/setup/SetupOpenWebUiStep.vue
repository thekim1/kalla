<script setup lang="ts">
import { computed, ref } from 'vue'
import { api } from '@/api'
import { isApiError } from '@/api/client'
import type { OpenWebUiTest, SetupState } from '@/api/types'
import InlineError from '@/components/ui/InlineError.vue'
import TextField from '@/components/ui/TextField.vue'
import UiButton from '@/components/ui/UiButton.vue'
import { useFormErrors } from '@/composables/useFormErrors'
import CopyValue from '@/platform/CopyValue.vue'
import { SETUP_WIZARD } from '@/utils/labels'
import { openWebUiCheckMessage, setupCompleteMessage } from '@/utils/messages'
import { problemMessage } from '@/utils/problem'
import SetupOpenWebUiResult from './SetupOpenWebUiResult.vue'
import SetupSecretField from './SetupSecretField.vue'
import {
  canSaveOpenWebUi, isLocked, openWebUiAllLocked, openWebUiCompleteRequestOf, openWebUiTestRequestOf, type OpenWebUiForm, type OpenWebUiTextField,
} from './setupValues'

// Step 4 (admin design §5.11, identity design §19 "Open WebUI"), shown only when the deployment runs Open WebUI: the redirect URI to
// register on its client, its values (a deployment's locked), Check (a back-channel check answered at once: the discovery document
// and whether the token endpoint accepts the client) and Save, which needs this session's passed test sign-in by a platform admin
// and does not close setup. The step is optional: Next skips it.
const props = defineProps<{
  state: SetupState
  test: OpenWebUiTest | null
  stale: boolean
  /** The session's latest kalla-admin test passed as a platform admin (Save needs it). */
  adminTestPassed: boolean
  /** After a save in this session: whether the check confirmed the client; null while nothing was saved. */
  savedVerified: boolean | null
  /** The wizard's handling of a 401 (back to the code) or 404 (setup closed); whether `error` was one of them. */
  lost: (error: unknown) => boolean
}>()
const emit = defineEmits<{ (e: 'checked', test: OpenWebUiTest): void; (e: 'saved', verified: boolean): void; (e: 'toSignIn'): void }>()
const form = defineModel<OpenWebUiForm>({ required: true })

const L = SETUP_WIZARD.openWebUi
/** The same text as the server's refusal `admin_test_required`. */
const ADMIN_TEST_FIRST = setupCompleteMessage('admin_test_required')
const S = SETUP_WIZARD.signIn
const openWebUi = computed(() => props.state.openWebUi)
const signIn = computed(() => openWebUi.value.signIn)
const locked = (field: OpenWebUiTextField) => isLocked(signIn.value, field)
const allLocked = computed(() => openWebUiAllLocked(signIn.value))
const saveable = computed(() => props.adminTestPassed && !allLocked.value && canSaveOpenWebUi(props.test, props.stale))

/** The provider address's default follows the sign-in provider (its discovery document), when that is known. */
const providerUrlDefault = computed(() => (signIn.value.providerUrl.source === 'default' ? signIn.value.providerUrl.value : null))
const providerUrlHint = computed(() => {
  if (locked('providerUrl')) return S.locked
  // Without a default (no sign-in provider known yet, not even from this session's test) the field needs an address: the example.
  return providerUrlDefault.value ? L.followsSignIn(providerUrlDefault.value) : L.providerUrlHint
})
function hintOf(field: 'clientId' | 'providerName', own?: string): string | undefined {
  if (locked(field)) return S.locked
  const value = signIn.value[field]
  const fallback = value.source === 'default' && value.value ? S.defaultHint(value.value) : undefined
  return [own, fallback].filter(Boolean).join(' ') || undefined
}

const errors = useFormErrors({ providerUrl: 'setup-owui-providerUrl' })
const checking = ref(false)
const checkError = ref<string | null>(null)
const saving = ref(false)
const saveError = ref<string | null>(null)

async function check(): Promise<void> {
  if (checking.value) return
  checkError.value = null
  const ok = errors.validate({
    providerUrl: !locked('providerUrl') && !form.value.providerUrl.trim() && !providerUrlDefault.value && L.providerUrlRequired,
  })
  if (!ok) return
  checking.value = true
  try {
    emit('checked', await api.setup.testOpenWebUi(openWebUiTestRequestOf(form.value, signIn.value)))
  } catch (e) {
    if (props.lost(e)) return
    if (isApiError(e) && e.problem?.code === 'setup_test_refused') checkError.value = openWebUiCheckMessage(e.problem.params?.reason ?? '')
    else {
      errors.applyServerError(e)
      checkError.value = errors.formError.value
    }
  } finally {
    checking.value = false
  }
}

async function save(): Promise<void> {
  const t = props.test
  if (saving.value || !saveable.value || !canSaveOpenWebUi(t, props.stale)) return
  saving.value = true
  saveError.value = null
  try {
    emit('saved', (await api.setup.completeOpenWebUi(openWebUiCompleteRequestOf(t))).clientVerified)
  } catch (e) {
    if (props.lost(e)) return
    saveError.value = isApiError(e) && e.problem?.code === 'setup_test_required' ? openWebUiCheckMessage(e.problem.params?.reason ?? '') : problemMessage(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <form class="grid gap-5" novalidate data-test="openwebui-step" @submit.prevent="check">
    <p class="text-fg-2">{{ L.lead }}</p>
    <div class="grid gap-1.5">
      <span class="font-medium">{{ L.redirectUri }}</span>
      <template v-if="openWebUi.redirectUri">
        <span class="text-small text-fg-3">{{ L.redirectLead }}</span>
        <CopyValue :value="openWebUi.redirectUri" :copy-label="L.copyRedirectUri" data-test="openwebui-redirect" />
      </template>
      <p v-else class="text-small text-fg-3" data-test="openwebui-no-redirect">{{ L.noRedirectUri }}</p>
    </div>
    <InlineError v-if="!adminTestPassed" tone="warn" :message="ADMIN_TEST_FIRST" data-test="admin-test-first">
      <UiButton class="mt-2" size="sm" icon="arrow_back" data-test="to-sign-in" @click="emit('toSignIn')">{{ L.toSignIn }}</UiButton>
    </InlineError>
    <p v-if="allLocked" class="text-small text-fg-2" data-test="openwebui-all-deployment">{{ L.allDeployment }}</p>
    <TextField
      id="setup-owui-providerUrl" v-model="form.providerUrl" :label="L.providerUrl" :hint="providerUrlHint" :error="errors.errors.providerUrl"
      :placeholder="providerUrlDefault ?? undefined" :disabled="locked('providerUrl')" inputmode="url" mono
    />
    <TextField id="setup-owui-clientId" v-model="form.clientId" :label="S.clientId" :hint="hintOf('clientId')" :disabled="locked('clientId')" mono />
    <SetupSecretField v-model="form" :secret="signIn.clientSecret" id-prefix="setup-owui" :removed-hint="L.secretRemoved" />
    <TextField
      id="setup-owui-providerName" v-model="form.providerName" :label="L.providerName" :hint="hintOf('providerName', L.providerNameHint)"
      :disabled="locked('providerName')"
    />
    <div class="grid gap-2">
      <div class="flex flex-wrap items-center gap-3">
        <UiButton type="submit" icon="fact_check" :loading="checking" data-test="openwebui-check">{{ L.check }}</UiButton>
        <span class="text-small text-fg-3">{{ L.checkHint }}</span>
      </div>
      <InlineError v-if="checkError" :message="checkError" data-test="openwebui-check-error" />
    </div>
    <SetupOpenWebUiResult :test="test" :stale="stale" />
    <div v-if="!allLocked" class="grid gap-2">
      <div class="flex flex-wrap items-center gap-3">
        <UiButton
          variant="primary" icon="check" :disabled="!saveable" :loading="saving" :aria-describedby="saveable ? undefined : 'setup-owui-save-needs'"
          data-test="openwebui-save" @click="save"
        >
          {{ L.save }}
        </UiButton>
        <span v-if="!saveable" id="setup-owui-save-needs" class="text-small text-fg-3">{{ adminTestPassed ? L.saveNeeds : ADMIN_TEST_FIRST }}</span>
      </div>
      <InlineError v-if="saveError" :message="saveError" data-test="openwebui-save-error" />
      <div v-if="savedVerified !== null" role="status" class="text-small" data-test="openwebui-saved">
        <p class="font-medium text-ok">{{ L.saved }}</p>
        <p>{{ savedVerified ? L.savedVerified : L.savedUnverified }}</p>
      </div>
    </div>
    <p class="text-small text-fg-3" data-test="openwebui-skip-hint">{{ L.skipHint }}</p>
  </form>
</template>
